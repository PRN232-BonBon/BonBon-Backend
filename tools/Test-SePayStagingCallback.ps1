param([Parameter(Mandatory=$true)][string]$PublicOrigin)
# Safe connectivity only; no key, real invoice, paymentId, success notification or database access.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Net.Http
$uri=$null
if(![Uri]::TryCreate($PublicOrigin,[UriKind]::Absolute,[ref]$uri) -or $uri.Scheme -ne 'https' -or !$uri.IsDefaultPort -or
 $uri.IsLoopback -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne '/' -or $uri.HostNameType -ne [UriHostNameType]::Dns -or
 $uri.Host -in @('bonbon.kitsuracloud.com','bonbon-api.kitsuracloud.com','bonbon-admin.kitsuracloud.com') -or
 $uri.Host.EndsWith('.bonbon.kitsuracloud.com')){throw 'Only an independently approved dedicated HTTPS staging origin is allowed.'}
$handler=[System.Net.Http.HttpClientHandler]::new();$handler.AllowAutoRedirect=$false;$handler.UseProxy=$false
$client=[System.Net.Http.HttpClient]::new($handler);$client.Timeout=[TimeSpan]::FromSeconds(15);$client.MaxResponseContentBufferSize=65536
try {
 $origin=$uri.GetLeftPart([UriPartial]::Authority)
 $request=[System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post,$origin+'/api/payments/sepay/ipn')
 $request.Content=[System.Net.Http.StringContent]::new('{"timestamp":1,"notification_type":"CONNECTIVITY_PROBE","order":null,"transaction":null}',[Text.Encoding]::UTF8,'application/json')
 try {$response=$client.SendAsync($request).GetAwaiter().GetResult()
  try {
   $marker=if($response.Headers.Contains('X-BonBon-SePay-Endpoint')){($response.Headers.GetValues('X-BonBon-SePay-Endpoint') -join '')}else{''}
   $ipnStatus=[int]$response.StatusCode
   if($marker -ne 'ipn-v1' -or $ipnStatus -notin @(401,503)){throw 'Unexpected IPN route/status. Do not mark callbacks verified.'}
   [pscustomobject]@{Endpoint='/api/payments/sepay/ipn';HttpStatus=$ipnStatus;RouteVerified=$true;PaymentEvidenceCreated=$false}|ConvertTo-Json -Compress
  }finally{$response.Dispose()}
 }finally{$request.Dispose()}
 $response=$client.GetAsync($origin+'/api/payments/sepay/return').GetAwaiter().GetResult()
 try {
  $marker=if($response.Headers.Contains('X-BonBon-SePay-Endpoint')){($response.Headers.GetValues('X-BonBon-SePay-Endpoint') -join '')}else{''}
  if($marker -ne 'return-v1' -or [int]$response.StatusCode -notin @(302,503)){throw 'Unexpected Return route/status.'}
  [pscustomobject]@{Endpoint='/api/payments/sepay/return';HttpStatus=[int]$response.StatusCode;RouteVerified=$true;RedirectFollowed=$false}|ConvertTo-Json -Compress
 }finally{$response.Dispose()}
 'Connectivity only: does not prove SePay delivery, IPN secret, REST reconciliation or successful payment.'
}catch{throw 'Callback connectivity check FAILED. No secret/body/certificate bypass or retry was used.'}
finally{$client.Dispose()}
