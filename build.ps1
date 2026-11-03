# Builds the single-file release exe into .\publish and writes its SHA256.
# The version comes from Directory.Build.props (the only place it is set).

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$props = Get-Content (Join-Path $PSScriptRoot "Directory.Build.props") -Raw
if ($props -notmatch '<Version>(\d+\.\d+\.\d+)</Version>') { throw "No <Version>X.Y.Z</Version> in Directory.Build.props" }
$version = $Matches[1]

dotnet publish src/PasteGuard/PasteGuard.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -o publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Join-Path $PSScriptRoot "publish\PasteGuard.exe"
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
"$hash  PasteGuard.exe" | Out-File (Join-Path $PSScriptRoot "publish\PasteGuard.exe.sha256") -Encoding ascii -NoNewline
Write-Host "Built $exe (version $version)"
Write-Host "SHA256 $hash"
