[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$BundleRoot = Split-Path -Parent $PSCommandPath
$RepoRoot = Split-Path -Parent (Split-Path -Parent $BundleRoot)
$DeploymentRoot = Join-Path $RepoRoot 'Deployment'
$ArtifactsRoot = Join-Path $DeploymentRoot 'artifacts'
$StageRoot = Join-Path $ArtifactsRoot 'staging\LocalProfanityCensor'
$PayloadRoot = Join-Path $BundleRoot 'Payload'
$ConfigsRoot = Join-Path $BundleRoot 'Configs'
$DictionariesRoot = Join-Path $BundleRoot 'Dictionaries'
$ScriptsRoot = Join-Path $BundleRoot 'Scripts'

$publishScript = Join-Path $DeploymentRoot 'Scripts\Publish-Core.ps1'
$stageScript = Join-Path $DeploymentRoot 'Scripts\Stage-Payload.ps1'
$setupScript = Join-Path $DeploymentRoot 'Scripts\Setup-AI.ps1'
$batchScript = Join-Path $DeploymentRoot 'Scripts\Invoke-CensorMediaBatch.ps1'
$configSource = Join-Path $RepoRoot 'src\LocalProfanityCensor.DotNet\Configs'
$dictionarySource = Join-Path $RepoRoot 'src\LocalProfanityCensor.DotNet\Dictionaries\profanity.example.yml'
$downloadModelScript = Join-Path $RepoRoot 'scripts\Download-FasterWhisperModel.ps1'

foreach ($path in @($PayloadRoot, $ConfigsRoot, $DictionariesRoot, $ScriptsRoot)) {
    New-Item -ItemType Directory -Force -Path $path | Out-Null
}

if (-not $SkipPublish) {
    & $publishScript -Configuration $Configuration -Runtime $Runtime -OutputRoot $ArtifactsRoot
}

& $stageScript -ArtifactsRoot $ArtifactsRoot -ConfigSource $configSource -DictionarySource $dictionarySource

if (Test-Path -LiteralPath $PayloadRoot) {
    Get-ChildItem -LiteralPath $PayloadRoot -Force | Remove-Item -Recurse -Force
}
if (Test-Path -LiteralPath $ConfigsRoot) {
    Get-ChildItem -LiteralPath $ConfigsRoot -Force | Remove-Item -Recurse -Force
}
if (Test-Path -LiteralPath $DictionariesRoot) {
    Get-ChildItem -LiteralPath $DictionariesRoot -Force | Remove-Item -Recurse -Force
}
if (Test-Path -LiteralPath $ScriptsRoot) {
    Get-ChildItem -LiteralPath $ScriptsRoot -Force | Remove-Item -Recurse -Force
}

Copy-Item -Path (Join-Path $StageRoot '*') -Destination $PayloadRoot -Recurse -Force
Copy-Item -Path (Join-Path $configSource '*') -Destination $ConfigsRoot -Recurse -Force
Copy-Item -Path $dictionarySource -Destination (Join-Path $DictionariesRoot 'profanity.example.yml') -Force
Copy-Item -Path $setupScript -Destination (Join-Path $ScriptsRoot 'Setup-AI.ps1') -Force
Copy-Item -Path $batchScript -Destination (Join-Path $ScriptsRoot 'Invoke-CensorMediaBatch.ps1') -Force
Copy-Item -Path $downloadModelScript -Destination (Join-Path $ScriptsRoot 'Download-FasterWhisperModel.ps1') -Force

Write-Host "Bundle built at $BundleRoot"
Write-Host "Payload staged to $PayloadRoot"