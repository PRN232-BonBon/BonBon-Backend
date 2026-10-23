param([string]$CredentialFile,[string]$OutputFile,[string]$ExpectedDatabaseHost)
$ErrorActionPreference='Stop'
$backendRoot=Split-Path $PSScriptRoot -Parent
if ((git -C $backendRoot branch --show-current) -ne 'feature/payment') { throw 'Expected feature/payment.' }
if (!$CredentialFile) { $CredentialFile=Join-Path $backendRoot 'BonBon.API/.payment-test.local.json' }
if (!$OutputFile) { $OutputFile=Join-Path $backendRoot 'BonBon.API/.payment-staging.local.json' }
if (Test-Path -LiteralPath $OutputFile) { Write-Output 'Existing staging config retained; no secrets displayed.'; exit 0 }
if ([string]::IsNullOrWhiteSpace($ExpectedDatabaseHost) -or $ExpectedDatabaseHost -ne $ExpectedDatabaseHost.Trim() -or
    [Uri]::CheckHostName($ExpectedDatabaseHost) -eq [UriHostNameType]::Unknown -or $ExpectedDatabaseHost -match '[,;/\\*]') {
 throw 'Supply an independently approved single ExpectedDatabaseHost; it is never inferred from credentials.'
}
$testCredentials=Get-Content -LiteralPath $CredentialFile -Raw | ConvertFrom-Json
$jwtKey=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
$settings=@{
 ConnectionStrings=@{DefaultConnection=$testCredentials.ConnectionString+';Pooling=true;Maximum Pool Size=2;Minimum Pool Size=0;Connection Idle Lifetime=30;Connection Pruning Interval=10;Command Timeout=10;Include Error Detail=false;Log Parameters=false'}
 Jwt=@{Key=$jwtKey;Issuer='BonBon.PaymentStaging';Audience='BonBon.PaymentStaging.Web';ExpiryMinutes=30;RefreshTokenDays=1}
 Payment=@{Enabled=$false;CheckoutMinutes=15}
 PaymentStaging=@{ExpectedDatabaseHost=$ExpectedDatabaseHost;PublicOrigin='';IpnUrl='';CallbacksVerified=$false;MerchantVerified=$false;ReconciliationPlanVerified=$false}
 VnPay=@{PaymentUrl='https://sandbox.vnpayment.vn/paymentv2/vpcpay.html';TmnCode='';HashSecret='';ReturnUrl=''}
 PaymentReturn=@{FrontendResultUrl='http://127.0.0.1:5289/payment/result'}
 Cors=@{AllowedOrigins=@('http://127.0.0.1:5289')}
 AllowedHosts='127.0.0.1;localhost'
 Logging=@{LogLevel=@{Default='Warning';'Microsoft.AspNetCore'='Warning';'Microsoft.EntityFrameworkCore.Database.Command'='Warning'}}
}
$settings | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputFile -Encoding utf8
Write-Output 'Dedicated local staging configuration created. Payment disabled; merchant/HTTPS values empty. Secrets not displayed.'
