param(
  [Parameter(Mandatory = $true)]
  [string]$BackendPath,
  [int]$Port = 5188
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$settingsPath = Join-Path $scriptDir 'client-settings.json'

if (-not (Test-Path $settingsPath)) {
  throw "Khong tim thay client-settings.json cung thu muc voi script ($scriptDir). Hay xin file nay rieng, khong commit len git."
}

if (-not (Test-Path $BackendPath)) {
  throw "BackendPath khong ton tai: $BackendPath"
}

$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json

# Ho tro 2 kieu file: full connection string hoac cac field roi
$connStr = $null
if ($settings.ConnectionStrings -and $settings.ConnectionStrings.DefaultConnection) {
  $connStr = $settings.ConnectionStrings.DefaultConnection
} elseif ($settings.ConnectionString) {
  $connStr = $settings.ConnectionString
} elseif ($settings.Host) {
  $port = if ($settings.Port) { $settings.Port } else { 55432 }
  $db = if ($settings.Database) { $settings.Database } else { 'bonbon_test' }
  $user = if ($settings.Username) { $settings.Username } else { 'bonbon_test_app' }
  $connStr = "Host=$($settings.Host);Port=$port;Database=$db;Username=$user;Password=$($settings.Password);SSL Mode=Disable;Include Error Detail=true"
}

if ([string]::IsNullOrWhiteSpace($connStr)) {
  throw "client-settings.json thieu connection string (ConnectionStrings.DefaultConnection)."
}

$jwtKey = $null
if ($settings.Jwt -and $settings.Jwt.Key) { $jwtKey = $settings.Jwt.Key }
elseif ($settings.JwtKey) { $jwtKey = $settings.JwtKey }

if ([string]::IsNullOrWhiteSpace($jwtKey) -or $jwtKey.Length -lt 32) {
  throw "client-settings.json thieu Jwt Key test (>= 32 ky tu). Khong dung JWT production."
}

$jwtIssuer = if ($settings.Jwt -and $settings.Jwt.Issuer) { $settings.Jwt.Issuer } else { 'BonBon.API' }
$jwtAudience = if ($settings.Jwt -and $settings.Jwt.Audience) { $settings.Jwt.Audience } else { 'BonBon.Client' }

# Luu env cu de khoi phuc khi dung
$backup = @{}
foreach ($name in @('ASPNETCORE_ENVIRONMENT','ASPNETCORE_URLS','ConnectionStrings__DefaultConnection','Jwt__Key','Jwt__Issuer','Jwt__Audience','Payment__Enabled','SePay__Enabled','VnPay__TmnCode','VnPay__HashSecret')) {
  $backup[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
  $env:ASPNETCORE_ENVIRONMENT = 'Development'
  $env:ASPNETCORE_URLS = "http://localhost:$Port"
  $env:ConnectionStrings__DefaultConnection = $connStr
  $env:Jwt__Key = $jwtKey
  $env:Jwt__Issuer = $jwtIssuer
  $env:Jwt__Audience = $jwtAudience
  # Luon tat thanh toan that khi chay DB test chung
  $env:Payment__Enabled = 'false'
  $env:SePay__Enabled = 'false'

  Write-Host "== BonBon test backend ==" -ForegroundColor Green
  Write-Host "Backend : $BackendPath"
  Write-Host "URL     : http://localhost:$Port/swagger"
  Write-Host "DB      : $($connStr -replace 'Password=[^;]*','Password=***')"
  Write-Host "JWT iss : $jwtIssuer / aud : $jwtAudience (key test rieng)"
  Write-Host "Luu y   : khong dung token/session production cho API test." -ForegroundColor Yellow

  dotnet run --project (Join-Path $BackendPath 'BonBon.API') --no-launch-profile --urls "http://localhost:$Port"
}
finally {
  foreach ($k in $backup.Keys) {
    if ($null -eq $backup[$k]) {
      [Environment]::SetEnvironmentVariable($k, $null, 'Process')
    } else {
      [Environment]::SetEnvironmentVariable($k, $backup[$k], 'Process')
    }
  }
  Write-Host "Da khoi phuc bien moi truong." -ForegroundColor DarkGray
}
