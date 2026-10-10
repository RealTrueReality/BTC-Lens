$ErrorActionPreference = 'Stop'
$lensProjectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $lensProjectRoot
try {
    Write-Host 'BTC Lens: http://127.0.0.1:5080'
    dotnet run --project src/BtcLens.Web
    if ($LASTEXITCODE -ne 0) { throw 'BTC Lens stopped with an error.' }
} finally { Pop-Location }
