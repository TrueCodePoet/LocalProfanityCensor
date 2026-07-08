[CmdletBinding()]
param(
    [string]$ProjectPath,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'

$ScriptRoot = Split-Path -Parent $PSCommandPath
$DeploymentRoot = Split-Path -Parent $ScriptRoot
$RepoRoot = Split-Path -Parent $DeploymentRoot

if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = Join-Path $RepoRoot 'src\LocalProfanityCensor.DotNet\LocalProfanityCensor.DotNet.csproj'
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $DeploymentRoot 'artifacts'
}

$publishDir = Join-Path $OutputRoot "publish\core"
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

Write-Host "Publishing LocalProfanityCensor core app to $publishDir"

dotnet publish $ProjectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir

Write-Host "Publish complete: $publishDir"
