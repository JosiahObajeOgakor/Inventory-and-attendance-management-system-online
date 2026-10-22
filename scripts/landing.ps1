<#
.SYNOPSIS  Serves the public landing page (client/landing-ui) locally, with its /api calls (the website chat) going to the LOCAL API started by dev.ps1.
.EXAMPLE   ./scripts/dev.ps1 -NoBrowser ; ./scripts/landing.ps1       # then open http://localhost:4300
.EXAMPLE   ./scripts/landing.ps1 -Stop
Nothing here talks to the live server: the chat uses the local test database.
#>
param([switch]$Stop, [int]$Port = 4300)
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Work = Join-Path $Root '.dev'
$pidFile = Join-Path $Work 'landing.pid'
if (Test-Path $pidFile) { Stop-Process -Id ([int](Get-Content $pidFile)) -Force -ErrorAction SilentlyContinue; Remove-Item $pidFile }
if ($Stop) { Write-Host 'Landing page stopped.'; return }

$app = Join-Path $Root 'client/landing-ui'
Push-Location $app
try {
    if (-not (Test-Path 'node_modules')) { Write-Host 'Installing landing page packages (first time only)...' -ForegroundColor Cyan; npm install --no-audit --no-fund | Out-Null }
    $p = Start-Process npx.cmd -ArgumentList "ng serve --port $Port --proxy-config proxy.conf.json" -WorkingDirectory $app `
        -RedirectStandardOutput (Join-Path $Work 'landing.log') -RedirectStandardError (Join-Path $Work 'landing.err.log') -WindowStyle Hidden -PassThru
    $p.Id | Set-Content $pidFile
} finally { Pop-Location }
for ($i = 0; $i -lt 120; $i++) {
    try { if ((Invoke-WebRequest "http://localhost:$Port" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) { break } } catch { Start-Sleep -Seconds 2 }
}
Write-Host "Landing page: http://localhost:$Port   (chat -> local API; logs in .dev/landing.log)" -ForegroundColor Green
