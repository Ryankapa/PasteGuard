# Builds the single-file release exe into .\publish and writes its SHA256.
param([string]$Version = "1.0.0")

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

dotnet publish src/PasteGuard/PasteGuard.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:Version=$Version `
    -o publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Join-Path $PSScriptRoot "publish\PasteGuard.exe"
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
"$hash  PasteGuard.exe" | Out-File (Join-Path $PSScriptRoot "publish\PasteGuard.exe.sha256") -Encoding ascii -NoNewline
Write-Host "Built $exe"
Write-Host "SHA256 $hash"
