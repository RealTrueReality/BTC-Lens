$ErrorActionPreference = 'Stop'
$lensProjectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $lensProjectRoot
try {
    dotnet build BtcLens.slnx -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet test BtcLens.slnx -c Release --no-build --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
} finally { Pop-Location }
