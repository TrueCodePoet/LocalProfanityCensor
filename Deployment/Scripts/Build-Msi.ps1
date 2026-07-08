[CmdletBinding()]
param(
    [string]$ArtifactsRoot,
    [string]$InstallerProject,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = 'Stop'

$ScriptRoot = Split-Path -Parent $PSCommandPath
$DeploymentRoot = Split-Path -Parent $ScriptRoot

if ([string]::IsNullOrWhiteSpace($ArtifactsRoot)) {
    $ArtifactsRoot = Join-Path $DeploymentRoot 'artifacts'
}

if ([string]::IsNullOrWhiteSpace($InstallerProject)) {
    $InstallerProject = Join-Path $DeploymentRoot 'Installer\LocalProfanityCensor.Installer.wixproj'
}

$stageDir = Join-Path $ArtifactsRoot "staging\LocalProfanityCensor"
if (-not (Test-Path $stageDir)) {
    throw "Staging payload not found: $stageDir. Run Publish-Core.ps1 and Stage-Payload.ps1 first."
}

$env:LPC_STAGE_DIR = $stageDir

Write-Host "Building MSI from staged payload: $stageDir"
dotnet build $InstallerProject -c $Configuration
