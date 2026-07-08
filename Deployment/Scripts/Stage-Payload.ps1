[CmdletBinding()]
param(
    [string]$ArtifactsRoot,
    [string]$ConfigSource,
    [string]$DictionarySource
)

$ErrorActionPreference = 'Stop'

$ScriptRoot = Split-Path -Parent $PSCommandPath
$DeploymentRoot = Split-Path -Parent $ScriptRoot
$RepoRoot = Split-Path -Parent $DeploymentRoot

if ([string]::IsNullOrWhiteSpace($ArtifactsRoot)) {
    $ArtifactsRoot = Join-Path $DeploymentRoot 'artifacts'
}

if ([string]::IsNullOrWhiteSpace($ConfigSource)) {
    $ConfigSource = Join-Path $RepoRoot 'src\LocalProfanityCensor.DotNet\Configs'
}

if ([string]::IsNullOrWhiteSpace($DictionarySource)) {
    $DictionarySource = Join-Path $RepoRoot 'src\LocalProfanityCensor.DotNet\Dictionaries\profanity.example.yml'
}

$publishDir = Join-Path $ArtifactsRoot "publish\core"
$stageDir = Join-Path $ArtifactsRoot "staging\LocalProfanityCensor"

if (-not (Test-Path $publishDir)) {
    throw "Publish output not found: $publishDir. Run Publish-Core.ps1 first."
}

New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $stageDir 'Configs') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $stageDir 'Dictionaries') | Out-Null

Copy-Item -Path (Join-Path $publishDir '*') -Destination $stageDir -Recurse -Force
Copy-Item -Path (Join-Path $ConfigSource '*') -Destination (Join-Path $stageDir 'Configs') -Recurse -Force
Copy-Item -Path $DictionarySource -Destination (Join-Path $stageDir 'Dictionaries\profanity.example.yml') -Force

Write-Host "Staged payload at $stageDir"
