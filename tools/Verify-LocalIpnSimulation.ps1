# Read-only SQL + authenticated GET status. Never sends or signs a callback.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$api=Join-Path $root 'BonBon.API'
Add-Type -Path (Join-Path $api 'bin/Release/net8.0/Npgsql.dll')
$settings=Get-Content (Join-Path $api '.payment-local.local.json') -Raw|ConvertFrom-Json
$accounts=Get-Content (Join-Path $api '.payment-local-accounts.local.json') -Raw|ConvertFrom-Json
$builder=[Npgsql.NpgsqlConnectionStringBuilder]::new($settings.ConnectionStrings.DefaultConnection)
if($builder.Host -ne '127.0.0.1' -or $builder.Port -ne 55432 -or $builder.Database -ne 'bonbon_payment_local' -or $builder.Username -ne 'bonbon_payment_local_runner') {throw 'Only the fixed local runtime target is permitted.'}
$health=Invoke-RestMethod http://127.0.0.1:5288/health -TimeoutSec 10
if($health.environment -ne 'PaymentLocal' -or $health.database -ne 'bonbon_payment_local' -or !$health.databaseVerified) {throw 'Unexpected API environment.'}
$rows=@()
$connection=[Npgsql.NpgsqlConnection]::new($builder.ConnectionString)
try {
 $connection.Open()
 $transaction=$connection.BeginTransaction()
 $command=$connection.CreateCommand();$command.Transaction=$transaction
 $command.CommandText="SET TRANSACTION READ ONLY; SET LOCAL statement_timeout='5s'; SET LOCAL lock_timeout='1s'"
 [void]$command.ExecuteNonQuery()
 $command.CommandText="SELECT current_database()='bonbon_payment_local' AND current_user='bonbon_payment_local_runner' AND inet_server_addr()='127.0.0.1'::inet AND inet_server_port()=55432 AND shobj_description(oid,'pg_database')='bonbon-payment-local:v1' FROM pg_database WHERE datname=current_database()"
 if(!$command.ExecuteScalar()) {throw 'Local database identity rejected.'}
 $command.CommandText=@'
 SELECT v.model,b.booking_id,b.status,p.payment_id,p.status,p.amount,p.needs_reconciliation,
   (SELECT count(*) FROM bonbon.payment_notification n WHERE n.payment_id=p.payment_id)
 FROM bonbon.vehicle v JOIN bonbon.booking b USING(vehicle_id) LEFT JOIN bonbon.payment p USING(booking_id)
 WHERE v.plate_number LIKE 'LOCAL-IPN-%' AND b.customer_account_id=@customer ORDER BY v.model
'@
 [void]$command.Parameters.AddWithValue('customer',[guid]$accounts.CustomerId)
 $reader=$command.ExecuteReader()
 while($reader.Read()) {
  $rows+=[pscustomobject]@{Case=$reader.GetString(0);BookingId=$reader.GetGuid(1);BookingStatus=$reader.GetString(2);PaymentId=$(if($reader.IsDBNull(3)){$null}else{$reader.GetGuid(3)});PaymentStatus=$(if($reader.IsDBNull(4)){'NotCreated'}else{$reader.GetString(4)});Amount=$(if($reader.IsDBNull(5)){$null}else{$reader.GetDecimal(5)});Reconciliation=$(if($reader.IsDBNull(6)){$null}else{$reader.GetBoolean(6)});AuditCount=$reader.GetInt64(7)}
 }
 $reader.Close();$transaction.Rollback()
} finally {$connection.Dispose()}
$login=Invoke-RestMethod http://127.0.0.1:5288/api/auth/login -Method Post -ContentType application/json -Body (@{email=$accounts.CustomerEmail;password=$accounts.CustomerPassword;deviceInfo='Read-only local IPN verification'}|ConvertTo-Json) -TimeoutSec 10
$headers=@{Authorization='Bearer '+$login.accessToken}
foreach($row in $rows) {
 if($row.PaymentId) {
  $status=Invoke-RestMethod ("http://127.0.0.1:5288/api/payments/"+$row.PaymentId) -Headers $headers -TimeoutSec 10
  $row|Add-Member ApiStatus $status.status
  $row|Add-Member ApiMatchesSql ($status.status -eq $row.PaymentStatus -and $status.amount -eq $row.Amount -and $status.bookingId -eq $row.BookingId.ToString())
 }
}
$rows|ConvertTo-Json -Depth 4
