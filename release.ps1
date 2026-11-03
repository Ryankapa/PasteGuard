# Cuts a new PasteGuard release. See docs/RELEASING.md.
#   .\release.ps1 -Bump patch            # 1.1.0 -> 1.1.1  (fixes)
#   .\release.ps1 -Bump minor            # 1.1.0 -> 1.2.0  (new features)
#   .\release.ps1 -Bump major            # 1.1.0 -> 2.0.0  (breaking changes)
#   .\release.ps1 -Version 1.4.0         # explicit version
#   add -Push to push main and the tag, which starts the GitHub release build.
[CmdletBinding()]
param(
    [ValidateSet("major", "minor", "patch")][string]$Bump,
    [string]$Version,
    [switch]$Push
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

function Fail($msg) { Write-Host "release: $msg" -ForegroundColor Red; exit 1 }
function Invoke-Git {
    # git writes warnings to stderr; Windows PowerShell would treat those as fatal errors under "Stop".
    $ErrorActionPreference = "Continue"
    & git.exe @args 2>&1 | ForEach-Object { Write-Host "$_" }
    if ($LASTEXITCODE -ne 0) { Fail "git $($args -join ' ') failed" }
}
# Read and write as UTF-8 (Windows PowerShell 5.1 would otherwise garble non-ASCII characters).
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Read-Text($path) { [IO.File]::ReadAllText($path, $utf8) }
function Write-Text($path, $text) { [IO.File]::WriteAllText($path, $text, $utf8) }

if (-not $Bump -and -not $Version) { Fail "pass -Bump major|minor|patch or -Version X.Y.Z" }
if ($Bump -and $Version) { Fail "pass -Bump or -Version, not both" }

if (git status --porcelain) { Fail "commit or discard your changes first (the working tree must be clean)" }
if ((git branch --show-current) -ne "main") { Fail "releases are cut from main" }

$propsPath = Join-Path $PSScriptRoot "Directory.Build.props"
$props = Read-Text $propsPath
if ($props -notmatch '<Version>(\d+)\.(\d+)\.(\d+)</Version>') { Fail "no <Version>X.Y.Z</Version> in Directory.Build.props" }
$current = [version]"$($Matches[1]).$($Matches[2]).$($Matches[3])"

if ($Bump) {
    $new = switch ($Bump) {
        "major" { [version]"$($current.Major + 1).0.0" }
        "minor" { [version]"$($current.Major).$($current.Minor + 1).0" }
        "patch" { [version]"$($current.Major).$($current.Minor).$($current.Build + 1)" }
    }
} else {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "version must look like 1.2.3" }
    $new = [version]$Version
}
if ($new -le $current) { Fail "new version $new must be higher than the current $current" }
$tag = "v$new"
if (git tag --list $tag) { Fail "tag $tag already exists" }

# CHANGELOG: the Unreleased notes become this version's notes (and the in-app update notes).
$changelogPath = Join-Path $PSScriptRoot "CHANGELOG.md"
$changelog = Read-Text $changelogPath
$m = [regex]::Match($changelog, '(?ms)^## \[Unreleased\][ \t]*\r?\n(.*?)(?=^## \[|\z)')
if (-not $m.Success) { Fail "CHANGELOG.md has no '## [Unreleased]' section" }
if (-not $m.Groups[1].Value.Trim()) { Fail "the Unreleased section in CHANGELOG.md is empty: describe what changed first" }
$date = Get-Date -Format "yyyy-MM-dd"
$nl = if ($changelog -match "`r`n") { "`r`n" } else { "`n" }
$changelog = $changelog.Remove($m.Index, $m.Length).Insert($m.Index,
    "## [Unreleased]$nl$nl## [$new] - $date$nl$($m.Groups[1].Value)")
Write-Text $changelogPath $changelog

$props = $props -replace '<Version>\d+\.\d+\.\d+</Version>', "<Version>$new</Version>"
Write-Text $propsPath $props

Write-Host "Testing $new..."
dotnet test -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Invoke-Git checkout -- $propsPath $changelogPath
    Fail "tests failed; version and changelog were put back"
}

Invoke-Git add $propsPath $changelogPath
Invoke-Git commit -q -m "Release $tag"
Invoke-Git tag -a $tag -m "PasteGuard $tag"
Write-Host "Released $tag locally ($current -> $new)." -ForegroundColor Green

if ($Push) {
    Invoke-Git push origin main $tag
    Write-Host "Pushed. GitHub Actions is building the release: https://github.com/Ryankapa/PasteGuard/actions"
} else {
    Write-Host "Push it with:  git push origin main $tag"
}
