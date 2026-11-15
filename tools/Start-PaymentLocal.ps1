param([string]$ConfigFile)
$ErrorActionPreference='Stop'
$backendRoot=Split-Path $PSScriptRoot -Parent
$branch=git -C $backendRoot branch --show-current
if($branch -notin @('review/payment-sandbox-web','feature/payment') -and $branch -notmatch '^feature/sepay-payment-integration(-[0-9]+)?$') {
 throw 'Use the reviewed Payment work branch.'
}
if(!$ConfigFile){$ConfigFile=Join-Path $backendRoot 'BonBon.API/.payment-local.local.json'}
if(!(Test-Path -LiteralPath $ConfigFile)){throw 'Prepare the isolated local configuration first.'}
if(Get-NetTCPConnection -State Listen -LocalPort 5288,5287 -ErrorAction SilentlyContinue){throw 'Local API port occupied; no process will be stopped.'}
$env:BONBON_PAYMENT_LOCAL_CONFIG=(Resolve-Path -LiteralPath $ConfigFile).Path
$env:ASPNETCORE_ENVIRONMENT='PaymentLocal'
$env:DOTNET_ENVIRONMENT='PaymentLocal'
$sepayFile=Join-Path $backendRoot 'BonBon.API/.payment-sepay.local.json'
if(Test-Path -LiteralPath $sepayFile){$env:BONBON_SEPAY_CONFIG=(Resolve-Path $sepayFile).Path;$env:PaymentLocal__IpnSimulationEnabled='false'}
Push-Location (Join-Path $backendRoot 'BonBon.API')
try { & dotnet 'bin/Release/net8.0/BonBon.API.dll' }
finally { Pop-Location }
