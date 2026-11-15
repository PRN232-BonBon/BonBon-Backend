param([switch]$ExecuteLocalSimulation)
# LOCAL SIMULATION ONLY. Creates fresh synthetic bookings through real APIs.
# Never calls the VNPay gateway or updates Payment/Booking states with SQL.
$ErrorActionPreference='Stop'
if(!$ExecuteLocalSimulation){throw 'Explicit -ExecuteLocalSimulation is required.'}
$root=Split-Path $PSScriptRoot -Parent
$api=Join-Path $root 'BonBon.API'
$config=Get-Content (Join-Path $api '.payment-local.local.json') -Raw|ConvertFrom-Json
$accounts=Get-Content (Join-Path $api '.payment-local-accounts.local.json') -Raw|ConvertFrom-Json
Add-Type -Path (Join-Path $api 'bin/Release/net8.0/Npgsql.dll')
$builder=[Npgsql.NpgsqlConnectionStringBuilder]::new($config.ConnectionStrings.DefaultConnection)
if($builder.Host -ne '127.0.0.1' -or $builder.Port -ne 55432 -or $builder.Database -ne 'bonbon_payment_local' -or $builder.Username -ne 'bonbon_payment_local_runner'){throw 'Local target rejected.'}
$health=Invoke-RestMethod http://127.0.0.1:5288/health -TimeoutSec 10
if($health.environment -ne 'PaymentLocal' -or $health.database -ne 'bonbon_payment_local' -or !$health.databaseVerified -or !$health.paymentEnabled -or !$config.PaymentLocal.IpnSimulationEnabled){throw 'Local API readiness rejected.'}
$run=[guid]::NewGuid().ToString('N')
$manifestPath=Join-Path $api ".payment-local-ipn-regression-$run.local.json"
$manifest=@{kind='LOCAL SIMULATION - NOT VNPAY CONFIRMATION';run=$run;startedAt=[DateTimeOffset]::Now.ToString('o');cases=@();results=@()}
function Save-Manifest { $manifest|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $manifestPath }
function Check([bool]$condition,[string]$message){if(!$condition){throw $message}}
$connection=[Npgsql.NpgsqlConnection]::new($builder.ConnectionString)
function Read-Scalar([string]$sql,[hashtable]$parameters=@{}) {
 $transaction=$connection.BeginTransaction()
 try {
  $cmd=$connection.CreateCommand();$cmd.Transaction=$transaction
  $cmd.CommandText="SET TRANSACTION READ ONLY; SET LOCAL statement_timeout='5s'; SET LOCAL lock_timeout='1s'"
  [void]$cmd.ExecuteNonQuery();$cmd.CommandText=$sql
  foreach($key in $parameters.Keys){[void]$cmd.Parameters.AddWithValue($key,$parameters[$key])}
  $value=$cmd.ExecuteScalar();$transaction.Rollback();return $value
 } finally {$transaction.Dispose()}
}
function Login([string]$who) {
 $response=Invoke-RestMethod http://127.0.0.1:5288/api/auth/login -Method Post -ContentType application/json -Body (@{email=$accounts.($who+'Email');password=$accounts.($who+'Password');deviceInfo='LOCAL SIMULATION regression'}|ConvertTo-Json) -TimeoutSec 15
 Check ($response.account.accountId -eq $accounts.($who+'Id')) 'Unexpected synthetic login identity.'
 return @{Authorization='Bearer '+$response.accessToken}
}
function Post([string]$path,[hashtable]$headers,$body) {
 return Invoke-RestMethod ('http://127.0.0.1:5288'+$path) -Method Post -Headers $headers -ContentType application/json -Body ($body|ConvertTo-Json -Depth 5) -TimeoutSec 20
}
function Snapshot([guid]$booking) {
 $sql=@'
SELECT json_build_object('bookingId',b.booking_id,'bookingStatus',b.status,'quote',b.quoted_rental_amount,
 'paymentId',p.payment_id,'paymentStatus',p.status,'amount',p.amount,'reconciliation',p.needs_reconciliation,
 'responseCode',p.response_code,'transactionStatus',p.transaction_status,
 'paymentCount',(SELECT count(*) FROM bonbon.payment x WHERE x.booking_id=b.booking_id),
 'successCount',(SELECT count(*) FROM bonbon.payment x WHERE x.booking_id=b.booking_id AND x.status='Succeeded'),
 'auditCount',(SELECT count(*) FROM bonbon.payment_notification n WHERE n.payment_id=p.payment_id))
FROM bonbon.booking b LEFT JOIN bonbon.payment p USING(booking_id) WHERE b.booking_id=@id
'@
 return (Read-Scalar $sql @{id=$booking}|ConvertFrom-Json)
}
function Verify([guid]$booking,[string]$paymentState,[string]$bookingState,[int]$audit,[string]$label,[string]$rsp) {
 $state=Snapshot $booking
 $status=Invoke-RestMethod ("http://127.0.0.1:5288/api/payments/"+$state.paymentId) -Headers $customer -TimeoutSec 10
 $detail=Invoke-RestMethod ("http://127.0.0.1:5288/api/bookings/"+$booking) -Headers $customer -TimeoutSec 10
 $apiMatch=$status.status -eq $state.paymentStatus -and $status.bookingId -eq $booking.ToString() -and $status.amount -eq $state.amount -and $status.requiresReconciliation -eq $state.reconciliation -and $detail.status -eq $state.bookingStatus -and $detail.quotedRentalAmount -eq $state.quote
 $pass=$state.paymentStatus -eq $paymentState -and $state.bookingStatus -eq $bookingState -and $state.auditCount -eq $audit -and $state.paymentCount -eq 1 -and $state.quote -eq 1300000 -and $state.amount -eq 1300000 -and !$state.reconciliation -and $apiMatch -and $state.successCount -eq $(if($paymentState -eq 'Succeeded'){1}else{0})
 $result=[ordered]@{case=$label;result=$(if($pass){'PASS'}else{'FAIL'});rspCode=$rsp;bookingId=$booking;paymentId=$state.paymentId;payment=$state.paymentStatus;booking=$state.bookingStatus;audit=$state.auditCount;paymentCount=$state.paymentCount;apiMatchesSql=$apiMatch;requiresReconciliation=$state.reconciliation}
 $manifest.results+=@($result);Save-Manifest
 Write-Output ($result|ConvertTo-Json -Compress)
 Check $pass "SQL/API assertion failed: $label"
}
function Send-Simulation([guid]$payment,[string]$scenario,[string]$expectedCode) {
 $headers=@{Authorization=$customer.Authorization;Origin='http://127.0.0.1:5289';'X-BonBon-Local-Simulation'='I_UNDERSTAND_LOCAL_SIMULATION'}
 $r=Post '/api/local/vnpay-ipn-simulation' $headers @{paymentId=$payment;scenario=$scenario}
 Check ($r.simulated -eq $true -and $r.ipnHttpStatus -eq 200 -and $r.rspCode -eq $expectedCode) "Unexpected simulated IPN response for $scenario"
}
try {
 $connection.Open()
 Check ([bool](Read-Scalar "SELECT current_database()='bonbon_payment_local' AND current_user='bonbon_payment_local_runner' AND inet_server_addr()='127.0.0.1'::inet AND inet_server_port()=55432 AND shobj_description(oid,'pg_database')='bonbon-payment-local:v1' FROM pg_database WHERE datname=current_database()")) 'Database identity rejected.'
 $protectedId=[guid]'2c6df813-72bb-421a-8843-222c383099b0'
 $protectedSql=@'
SELECT md5(to_jsonb(p)::text||to_jsonb(b)::text||COALESCE((SELECT jsonb_agg(to_jsonb(n) ORDER BY n.payment_notification_id)::text FROM bonbon.payment_notification n WHERE n.payment_id=p.payment_id),''))
FROM bonbon.payment p JOIN bonbon.booking b USING(booking_id) WHERE p.payment_id=@id
'@
 $originalHash=Read-Scalar $protectedSql @{id=$protectedId}
 Check (![string]::IsNullOrWhiteSpace($originalHash)) 'Previously confirmed receipt missing.'
 $customer=Login 'Customer';$owner=Login 'Owner';Save-Manifest
 foreach($scenario in @('wrong-amount','bad-signature','duplicate','failure','cancel')) {
  $fixture=@($accounts.SimulationCases|Where-Object scenario -eq $scenario)
  Check ($fixture.Count -eq 1) 'Expected one synthetic fixture vehicle per case.'
  $vehicle=[guid]$fixture[0].vehicleId
  Check ([bool](Read-Scalar "SELECT EXISTS(SELECT 1 FROM bonbon.vehicle WHERE vehicle_id=@vehicle AND owner_account_id=@owner AND plate_number LIKE 'LOCAL-IPN-%' AND status='Available')" @{vehicle=$vehicle;owner=[guid]$accounts.OwnerId})) 'Synthetic vehicle rejected.'
  $start=[DateTimeOffset]::new([DateTime]::UtcNow.Date.AddDays(70).AddHours(2),[TimeSpan]::Zero)
  $request=@{vehicleId=$vehicle.ToString();rentalType='Daily';startAt=$start.ToString('o');endAt=$start.AddDays(2).ToString('o')}
  $quote=Post '/api/bookings/quote' $customer $request
  Check ($quote.quotedRentalAmount -eq 1300000) 'Unexpected fresh quote.'
  $booking=Post '/api/bookings' $customer $request
  Check ($booking.status -eq 'PendingApproval') 'Booking did not start PendingApproval.'
  $manifest.cases+=@(@{scenario=$scenario;bookingId=$booking.bookingId;vehicleId=$vehicle;idempotencyKey="local_ipn_$($run)_$scenario"});Save-Manifest
  $approved=Post ("/api/bookings/"+$booking.bookingId+'/approve') $owner $null
  Check ($approved.status -eq 'AwaitingPayment') 'Owner approval failed.'
  $paymentHeaders=@{Authorization=$customer.Authorization;'Idempotency-Key'="local_ipn_$($run)_$scenario"}
  $payment=Post '/api/payments' $paymentHeaders @{bookingId=$booking.bookingId}
  Check ($payment.bookingId -eq $booking.bookingId -and $payment.status -eq 'Pending' -and $payment.amount -eq 1300000) 'Fresh payment invalid.'
  # Never print or follow checkoutUrl: it contains merchant/signature parameters.
  $manifest.cases[-1].paymentId=$payment.paymentId;Save-Manifest
  $fresh=Snapshot ([guid]$booking.bookingId)
  Check ($fresh.paymentCount -eq 1 -and $fresh.auditCount -eq 0 -and $fresh.bookingStatus -eq 'AwaitingPayment' -and $fresh.paymentStatus -eq 'Pending') 'Case is not fresh.'
  switch($scenario) {
   'wrong-amount' { Send-Simulation ([guid]$payment.paymentId) $scenario '04';Verify ([guid]$booking.bookingId) 'Pending' 'AwaitingPayment' 0 $scenario '04' }
   'bad-signature' { Send-Simulation ([guid]$payment.paymentId) $scenario '97';Verify ([guid]$booking.bookingId) 'Pending' 'AwaitingPayment' 0 $scenario '97' }
   'duplicate' {
    Send-Simulation ([guid]$payment.paymentId) 'success' '00';Verify ([guid]$booking.bookingId) 'Succeeded' 'Confirmed' 1 'duplicate:initial-success-on-new-booking' '00'
    foreach($n in @(1,2)){Send-Simulation ([guid]$payment.paymentId) 'duplicate' '02';Verify ([guid]$booking.bookingId) 'Succeeded' 'Confirmed' 1 "duplicate:replay-$n" '02'}
   }
   'failure' { Send-Simulation ([guid]$payment.paymentId) $scenario '00';Verify ([guid]$booking.bookingId) 'Failed' 'AwaitingPayment' 1 $scenario '00' }
   'cancel' { Send-Simulation ([guid]$payment.paymentId) $scenario '00';Verify ([guid]$booking.bookingId) 'Cancelled' 'AwaitingPayment' 1 $scenario '00' }
  }
 }
 $unchanged=(Read-Scalar $protectedSql @{id=$protectedId}) -eq $originalHash
 $manifest.protectedPreviousReceiptUnchanged=$unchanged;Save-Manifest
 Check $unchanged 'Previously confirmed receipt changed during test.'
 $manifest.completedAt=[DateTimeOffset]::Now.ToString('o');Save-Manifest
 Write-Output 'LOCAL SIMULATION complete; previous confirmed receipt/booking/audit unchanged. No real VNPay request.'
} catch {
 $manifest.failed=$true;Save-Manifest
 # Do not echo raw HTTP/SQL exception bodies, URLs or credentials.
 Write-Error 'LOCAL SIMULATION stopped. Inspect the ignored run manifest; sensitive exception details suppressed.'
 exit 1
} finally { $connection.Dispose() }
