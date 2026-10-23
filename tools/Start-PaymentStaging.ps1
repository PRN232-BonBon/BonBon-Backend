param([string]$ConfigFile)
$ErrorActionPreference='Stop'
$backendRoot=Split-Path $PSScriptRoot -Parent
if ((git -C $backendRoot branch --show-current) -ne 'feature/payment') { throw 'Expected feature/payment.' }
if (!$ConfigFile) { $ConfigFile=Join-Path $backendRoot 'BonBon.API/.payment-staging.local.json' }
if (!(Test-Path -LiteralPath $ConfigFile)) { throw 'Initialize the dedicated local config first.' }
$env:BONBON_PAYMENT_STAGING_CONFIG=(Resolve-Path -LiteralPath $ConfigFile).Path
$env:ASPNETCORE_ENVIRONMENT='PaymentStaging'
$env:DOTNET_ENVIRONMENT='PaymentStaging'
Push-Location (Join-Path $backendRoot 'BonBon.API')
try {
 & dotnet 'bin/Release/net8.0/BonBon.API.dll' --urls 'http://127.0.0.1:5288'
 if ($LASTEXITCODE -ne 0) { throw 'Payment staging API exited unsuccessfully; no fallback process is started.' }
}
finally { Pop-Location }
