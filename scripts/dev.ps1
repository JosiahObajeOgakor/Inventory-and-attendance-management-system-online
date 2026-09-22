<#
.SYNOPSIS  Starts the whole stack for local development: MySQL (Docker) + API + UI.
.EXAMPLE   ./scripts/dev.ps1                 # start everything, open the browser
.EXAMPLE   ./scripts/dev.ps1 -ImportLocal    # also copy the data from your local SQL Server (LocalDB) databases
.EXAMPLE   ./scripts/dev.ps1 -Stop           # stop the API/UI and the database container
DEV ONLY: throwaway passwords below. Never reuse them anywhere real.
#>
param([switch]$ImportLocal, [switch]$Stop, [switch]$NoBrowser)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Work = Join-Path $Root '.dev'
$Container = 'inv-db'; $DbPort = 3308; $ApiPort = 5000; $UiPort = 4200
$DbPass = 'devpass'; $SetupToken = 'dev-setup-token'
$AdminUser = 'admin'; $AdminPass = 'ChangeMe1234'
New-Item -ItemType Directory -Force $Work | Out-Null

function Stop-Tracked {
    foreach ($f in 'api.pid', 'ui.pid') {
        $p = Join-Path $Work $f
        if (Test-Path $p) { Stop-Process -Id ([int](Get-Content $p)) -Force -ErrorAction SilentlyContinue; Remove-Item $p }
    }
}

if ($Stop) {
    Stop-Tracked
    docker stop $Container 2>$null | Out-Null
    Write-Host 'Stopped the API, the UI and the database container (data is kept).'
    return
}

foreach ($tool in 'docker', 'dotnet', 'node') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool is not installed or not on PATH." }
}
docker info 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Docker is not running. Start Docker Desktop and try again.' }

Stop-Tracked

# ---- 1. database -------------------------------------------------------------------------------
Write-Host '1/4 Database...' -ForegroundColor Cyan
$exists = docker ps -a --filter "name=^$Container$" --format '{{.Names}}'
if ($exists) { docker start $Container | Out-Null }
else { docker run -d --name $Container -e MYSQL_ROOT_PASSWORD=$DbPass -p "${DbPort}:3306" mysql:8.4 | Out-Null }
for ($i = 0; $i -lt 60; $i++) {
    docker exec $Container mysql -uroot "-p$DbPass" -e 'SELECT 1' 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { break }
    Start-Sleep -Seconds 2
}
if ($LASTEXITCODE -ne 0) { throw 'MySQL did not become ready.' }

# ---- 2. API ------------------------------------------------------------------------------------
Write-Host '2/4 API...' -ForegroundColor Cyan
dotnet build (Join-Path $Root 'src/Inventory.Api') --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'API build failed.' }
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = "http://localhost:$ApiPort"
$env:ConnectionStrings__Default = "Server=127.0.0.1;Port=$DbPort;User=root;Password=$DbPass;"
$env:Setup__Token = $SetupToken
$env:RateLimit__LoginPerMinute = '200'
# Secrets (OpenAI key, Zoho password) live in .dev/secrets.env, which git ignores. One KEY=value per line, e.g. OpenAI__ApiKey=...
$secretsFile = Join-Path $Work 'secrets.env'
if (Test-Path $secretsFile) {
    foreach ($line in Get-Content $secretsFile) {
        $t = $line.Trim()
        if ($t -eq '' -or $t.StartsWith('#')) { continue }
        $eq = $t.IndexOf('=')
        if ($eq -lt 1) { continue }
        $name = $t.Substring(0, $eq).Trim(); $val = $t.Substring($eq + 1).Trim()
        if ($name -match '^[A-Za-z_][A-Za-z0-9_]*$') { Set-Item -Path "env:$name" -Value $val }
    }
    Write-Host '   loaded secrets from .dev/secrets.env' -ForegroundColor DarkGray
}
$api = Start-Process dotnet -ArgumentList "run --project `"$(Join-Path $Root 'src/Inventory.Api')`" --no-build --no-launch-profile" `
    -RedirectStandardOutput (Join-Path $Work 'api.log') -RedirectStandardError (Join-Path $Work 'api.err.log') -WindowStyle Hidden -PassThru
$api.Id | Set-Content (Join-Path $Work 'api.pid')
$up = $false
for ($i = 0; $i -lt 60 -and -not $up; $i++) {
    Start-Sleep -Seconds 1
    try { $up = (Invoke-RestMethod "http://localhost:$ApiPort/api/health").status -eq 'healthy' } catch { }
}
if (-not $up) { throw "API did not start. See $Work\api.log" }

# first administrator (harmless if one already exists: the API answers 409)
$h = @{ 'Content-Type' = 'application/json'; 'X-Requested-With' = 'inventory-ui' }
try {
    Invoke-RestMethod "http://localhost:$ApiPort/api/setup/first-admin" -Method Post -Headers $h `
        -Body (@{ setupToken = $SetupToken; fullName = 'Dev Admin'; username = $AdminUser; password = $AdminPass } | ConvertTo-Json) | Out-Null
    Write-Host "     created admin '$AdminUser'"
} catch { Write-Host '     admin already exists' }

# ---- optional: copy the existing SQL Server (LocalDB) data ------------------------------------
if ($ImportLocal) {
    Write-Host '     importing data from LocalDB...' -ForegroundColor Cyan
    $sql = 'Server=(localdb)\MSSQLLocalDB;Integrated Security=True;TrustServerCertificate=True;Encrypt=False;'
    $my = "Server=127.0.0.1;Port=$DbPort;User=root;Password=$DbPass;"
    foreach ($c in @(@('chewypets', 'StockDeskDB', 'inventory_chewypets'), @('candid', 'CandidPurrfectDB', 'inventory_candid'))) {
        dotnet run --project (Join-Path $Root 'tools/Inventory.MigrationTool') --no-build -- --company $c[0] `
            --home-source "${sql}Database=StockDeskDB;" --source "${sql}Database=$($c[1]);" `
            --target "${my}Database=$($c[2]);" --identity "${my}Database=inventory_identity;" 2>&1 | Select-String 'PASS|FAIL|Refusing' | ForEach-Object { Write-Host "     $($c[0]): $_" }
    }
}

# ---- 3. UI -------------------------------------------------------------------------------------
Write-Host '3/4 UI...' -ForegroundColor Cyan
$ui = Join-Path $Root 'client/inventory-ui'
# Some tools (vite/ng serve) can't handle '#' in a path, so work through a junction that has none.
$uiWork = $ui
if ($Root -match '#') {
    $link = Join-Path $env:LOCALAPPDATA 'inventory-dev-link'
    if (-not (Test-Path $link)) { New-Item -ItemType Junction -Path $link -Target $Root | Out-Null }
    $uiWork = Join-Path $link 'client/inventory-ui'
}
Push-Location $uiWork
try {
    if (-not (Test-Path 'node_modules')) { npm install --legacy-peer-deps --no-audit --no-fund | Out-Null }
    if ($Root -match '#') {
        npx ng build | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'UI build failed.' }
        $srv = Start-Process node -ArgumentList "`"$(Join-Path $Root 'scripts/static-proxy.js')`" `"$(Join-Path $uiWork 'dist/inventory-ui/browser')`" $UiPort $ApiPort" `
            -RedirectStandardOutput (Join-Path $Work 'ui.log') -RedirectStandardError (Join-Path $Work 'ui.err.log') -WindowStyle Hidden -PassThru
    } else {
        $srv = Start-Process npx -ArgumentList "ng serve --port $UiPort" -WorkingDirectory $uiWork `
            -RedirectStandardOutput (Join-Path $Work 'ui.log') -RedirectStandardError (Join-Path $Work 'ui.err.log') -WindowStyle Hidden -PassThru
    }
    $srv.Id | Set-Content (Join-Path $Work 'ui.pid')
} finally { Pop-Location }

for ($i = 0; $i -lt 90; $i++) {
    try { if ((Invoke-WebRequest "http://localhost:$UiPort" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) { break } } catch { Start-Sleep -Seconds 1 }
}

Write-Host '4/4 Ready.' -ForegroundColor Green
Write-Host "     UI   http://localhost:$UiPort   sign in: $AdminUser / $AdminPass  (dev only)"
Write-Host "     API  http://localhost:$ApiPort/api/health"
Write-Host "     Logs $Work   ·   Stop with: ./scripts/dev.ps1 -Stop"
if (-not $NoBrowser) { Start-Process "http://localhost:$UiPort" }
