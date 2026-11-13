<#
.SYNOPSIS
    Builds the release artifact for a GitHub release: a self-contained, single-file win-x64
    publish of str-amp, zipped up ready to attach to the release.

.DESCRIPTION
    Mirrors what WindowsAppUpdater and WindowsAppInstaller expect on the other end: the zip's
    root (or one wrapping folder - both are handled) contains Stramp.App.exe alongside whatever
    loose native files it needs (currently just DfxBridge.dll). Bump AppVersion.Current in
    src/Stramp.Core/Updates/AppVersion.cs to match the release tag before running this.

.EXAMPLE
    ./publish.ps1
    Produces publish/stramp-2.0.0.zip from the repo root.
#>

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$versionFile = Join-Path $repoRoot "src/Stramp.Core/Updates/AppVersion.cs"
$versionMatch = Select-String -Path $versionFile -Pattern 'Current = "([^"]+)"'
if (-not $versionMatch) {
    throw "Couldn't read AppVersion.Current from $versionFile"
}
$version = $versionMatch.Matches[0].Groups[1].Value

$publishDir = Join-Path $repoRoot "publish/stramp"
$zipPath = Join-Path $repoRoot "publish/stramp-$version.zip"

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host "Publishing str-amp $version..."
dotnet publish "$repoRoot/src/Stramp.App/Stramp.App.csproj" -c Release -r win-x64 -o $publishDir `
    -p:SelfContained=true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true

# Debug symbols aren't useful to a release zip and the native ones alone run into the tens of MB.
Get-ChildItem $publishDir -Filter "*.pdb" | Remove-Item -Force

Write-Host "Zipping to $zipPath..."
Compress-Archive -Path "$publishDir/*" -DestinationPath $zipPath

Write-Host "Done: $zipPath"
Write-Host "Attach this to the v$version GitHub release."
