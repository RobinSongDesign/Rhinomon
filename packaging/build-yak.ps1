<#
.SYNOPSIS
  Builds Rhinomon.rhp and packs it into a Yak package.

.DESCRIPTION
  One script for local releases and CI: Release build with warnings as errors,
  a staging folder holding exactly manifest.yml + icon + .rhp, then `yak build`.
  The manifest version is replaced by -Version, so a prerelease tag such as
  0.2.0-beta.1 needs no file edits.

  The yak CLI is downloaded from McNeel when -YakPath is not given, so no Rhino
  installation is required.

.EXAMPLE
  pwsh packaging/build-yak.ps1                      # version from the csproj
  pwsh packaging/build-yak.ps1 -Version 0.2.0-beta.1
#>
param(
    [string]$Version,
    [string]$OutDir = "dist",
    [string]$YakPath,
    # The distribution tag the package must carry. rh8_0 = installs on every
    # Rhino 8; anything else means the RhinoCommon pin in the csproj moved.
    [string]$ExpectedTag = "rh8_0-win"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root "src/Rhinomon/Rhinomon.csproj"

if (-not $Version) {
    $Version = (Select-Xml -Path $csproj -XPath "//Version").Node.InnerText.Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw "Version '$Version' is not x.y.z or x.y.z-prerelease."
}

dotnet build $csproj -c Release -warnaserror -v minimal "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }

$rhp = Join-Path $root "src/Rhinomon/bin/Release/net7.0-windows/Rhinomon.rhp"
if (-not (Test-Path $rhp)) { throw "Build output not found: $rhp" }

$OutDir = [System.IO.Path]::GetFullPath($OutDir)
$stage = Join-Path $OutDir "stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Get-ChildItem $OutDir -Filter *.yak -ErrorAction SilentlyContinue | Remove-Item -Force

Copy-Item $rhp $stage
Copy-Item (Join-Path $PSScriptRoot "rhinomon.png") $stage
$manifest = Get-Content (Join-Path $PSScriptRoot "manifest.yml") -Raw -Encoding utf8
if ($manifest -notmatch '(?m)^version:\s*\S+\s*$') { throw "manifest.yml has no version line." }
$manifest = $manifest -replace '(?m)^version:\s*\S+[ \t]*$', "version: $Version"
# UTF-8 without BOM: the description contains non-ASCII punctuation.
[System.IO.File]::WriteAllText((Join-Path $stage "manifest.yml"), $manifest, [System.Text.UTF8Encoding]::new($false))

if (-not $YakPath) {
    $YakPath = Join-Path $OutDir "yak.exe"
    if (-not (Test-Path $YakPath)) {
        Invoke-WebRequest "https://files.mcneel.com/yak/tools/0.15.1/win-x64/yak.exe" -OutFile $YakPath
    }
}

Push-Location $stage
try {
    & $YakPath build --platform win
    if ($LASTEXITCODE -ne 0) { throw "yak build failed." }
}
finally {
    Pop-Location
}

$packages = @(Get-ChildItem $stage -Filter *.yak)
if ($packages.Count -ne 1) { throw "Expected exactly one .yak in $stage, found $($packages.Count)." }
$package = Move-Item $packages[0].FullName $OutDir -Force -PassThru

$expectedName = "rhinomon-$Version-$ExpectedTag.yak"
if ($package.Name -ne $expectedName) {
    throw "Package is named '$($package.Name)', expected '$expectedName'. A different tag changes which Rhino versions can install it."
}

Write-Host "Built $($package.FullName)"
if ($env:GITHUB_OUTPUT) {
    "package=$($package.FullName)" >> $env:GITHUB_OUTPUT
    "version=$Version" >> $env:GITHUB_OUTPUT
}
