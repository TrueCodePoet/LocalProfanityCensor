[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InputRoot,
    [Parameter(Mandatory = $true)]
    [string]$HoldingRoot,
    [int]$MaxFilesPerRun = 1,
    [switch]$DryRun,
    [switch]$KeepWork,
    [switch]$Repeat,
    [int]$RepeatIntervalMinutes = 15,
    [ValidateSet('cpu','gpu')]
    [string]$AIProfile = 'gpu',
    [string]$PythonPath = 'python',
    [string]$MediaPythonPath,
    [string]$HuggingFaceCacheRoot,
    [switch]$RunSetupAI,
    [switch]$SkipModelPreflight
)

$ErrorActionPreference = 'Stop'

$BundleRoot = Split-Path -Parent $PSCommandPath
$PayloadRoot = Join-Path $BundleRoot 'Payload'
$ScriptsRoot = Join-Path $BundleRoot 'Scripts'
$batchScript = Join-Path $ScriptsRoot 'Invoke-CensorMediaBatch.ps1'
$configPath = Join-Path $BundleRoot 'Configs\production.demucs.depth.censored-option.yml'
$dictionaryPath = Join-Path $BundleRoot 'Dictionaries\profanity.example.yml'

if (-not (Test-Path -LiteralPath $batchScript)) {
    throw "Bundle batch script not found: $batchScript. Run Build-Bundle.ps1 first."
}

if (-not (Test-Path -LiteralPath $PayloadRoot)) {
    throw "Bundle payload folder not found: $PayloadRoot. Run Build-Bundle.ps1 first."
}

$invokeParams = @{
    InputRoot = $InputRoot
    HoldingRoot = $HoldingRoot
    PayloadSource = $PayloadRoot
    ConfigPath = $configPath
    DictionaryPath = $dictionaryPath
    MaxFilesPerRun = $MaxFilesPerRun
    AIProfile = $AIProfile
    PythonPath = $PythonPath
}

if ($DryRun) { $invokeParams.DryRun = $true }
if ($KeepWork) { $invokeParams.KeepWork = $true }
if ($Repeat) { $invokeParams.Repeat = $true }
if ($RepeatIntervalMinutes -gt 0) { $invokeParams.RepeatIntervalMinutes = $RepeatIntervalMinutes }
if ($RunSetupAI) { $invokeParams.RunSetupAI = $true }
if ($SkipModelPreflight) { $invokeParams.SkipModelPreflight = $true }
if (-not [string]::IsNullOrWhiteSpace($MediaPythonPath)) { $invokeParams.MediaPythonPath = $MediaPythonPath }
if (-not [string]::IsNullOrWhiteSpace($HuggingFaceCacheRoot)) { $invokeParams.HuggingFaceCacheRoot = $HuggingFaceCacheRoot }

& $batchScript @invokeParams
