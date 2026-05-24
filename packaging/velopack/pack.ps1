# Build a Velopack release for Floss (installers only — no auto-update wiring).
#
# Usage:
#   .\packaging\velopack\pack.ps1 win-x64
#   .\packaging\velopack\pack.ps1 linux-x64   # on Linux/WSL
param(
    [Parameter(Position = 0)]
    [ValidateSet("linux-x64", "win-x64", "win-arm64", "osx-x64", "osx-arm64")]
    [string]$Rid = "win-x64"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$Channel = "$Rid-beta"
$PackId = "FlossPaint"
$PackTitle = "Floss"
$Icon = Join-Path $Root "packaging\icon.png"
$Project = Join-Path $Root "src\Floss.App\Floss.App.csproj"

$MainExe = switch ($Rid) {
    { $_ -like "win-*" } { "Floss.exe" }
    default { "Floss" }
}

$Version = if ($env:FLOSS_VERSION) { $env:FLOSS_VERSION } else {
    (dotnet msbuild $Project -getProperty:Version -nologo).Trim()
}

$Publish = Join-Path $Root "artifacts\publish\$Rid"
$Output = Join-Path $Root "artifacts\velopack\$Channel"

Write-Host "==> Floss Velopack pack"
Write-Host "    version : $Version"
Write-Host "    rid     : $Rid"
Write-Host "    channel : $Channel"
Write-Host "    output  : $Output"

if (Test-Path $Publish) { Remove-Item -Recurse -Force $Publish }
if (Test-Path $Output) { Remove-Item -Recurse -Force $Output }
New-Item -ItemType Directory -Force -Path (Split-Path $Output) | Out-Null

Write-Host "==> dotnet publish"
dotnet publish $Project `
    -c Release `
    -r $Rid `
    --self-contained true `
    -p:PublishTrimmed=false `
    -o $Publish

Write-Host "==> vpk pack"
Push-Location $Root
try {
    $env:DOTNET_ROLL_FORWARD = if ($env:DOTNET_ROLL_FORWARD) { $env:DOTNET_ROLL_FORWARD } else { "LatestMajor" }
    dotnet tool restore
    dotnet tool run vpk pack `
        --packId $PackId `
        --packVersion $Version `
        --packTitle $PackTitle `
        --packDir $Publish `
        --mainExe $MainExe `
        --icon $Icon `
        --channel $Channel `
        --outputDir $Output
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "Done. Ship Setup.exe / Portable.zip from:"
Write-Host "  $Output"
Get-ChildItem $Output
