[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InputRoot,

    [Parameter(Mandatory = $true)]
    [string]$HoldingRoot,

    [string]$StateRoot,
    [string]$ArchiveRoot,
    [string]$LocalWorkRoot,
    [string]$MediaPythonPath,
    [string]$HuggingFaceCacheRoot,

    [int]$MaxFilesPerRun = 1,
    [switch]$Repeat,
    [switch]$ReplaceOriginal,
    [switch]$DryRun,
    [switch]$KeepWork,
    [switch]$RunSetupAI,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$PublishScript = Join-Path $RepoRoot 'Deployment\Scripts\Publish-Core.ps1'
$StageScript = Join-Path $RepoRoot 'Deployment\Scripts\Stage-Payload.ps1'
$BatchScript = Join-Path $RepoRoot 'Deployment\Scripts\Invoke-CensorMediaBatch.ps1'

if (-not $SkipPublish) {
    & $PublishScript
    & $StageScript
}

$batchArgs = @(
    '-InputRoot', $InputRoot,
    '-HoldingRoot', $HoldingRoot,
    '-MaxFilesPerRun', $MaxFilesPerRun
)

if (-not [string]::IsNullOrWhiteSpace($StateRoot)) { $batchArgs += @('-StateRoot', $StateRoot) }
if (-not [string]::IsNullOrWhiteSpace($ArchiveRoot)) { $batchArgs += @('-ArchiveRoot', $ArchiveRoot) }
if (-not [string]::IsNullOrWhiteSpace($LocalWorkRoot)) { $batchArgs += @('-LocalWorkRoot', $LocalWorkRoot) }
if (-not [string]::IsNullOrWhiteSpace($MediaPythonPath)) { $batchArgs += @('-MediaPythonPath', $MediaPythonPath) }
if (-not [string]::IsNullOrWhiteSpace($HuggingFaceCacheRoot)) { $batchArgs += @('-HuggingFaceCacheRoot', $HuggingFaceCacheRoot) }
if ($Repeat) { $batchArgs += '-Repeat' }
if ($ReplaceOriginal) { $batchArgs += '-ReplaceOriginal' }
if ($DryRun) { $batchArgs += '-DryRun' }
if ($KeepWork) { $batchArgs += '-KeepWork' }
if ($RunSetupAI) { $batchArgs += '-RunSetupAI' }

& $BatchScript @batchArgs