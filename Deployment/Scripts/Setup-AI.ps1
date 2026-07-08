[CmdletBinding()]
param(
    [ValidateSet('cpu','gpu')]
    [string]$Profile = 'gpu',
    [string]$PythonPath = 'python',
    [string]$InstallRoot = "$env:ProgramData\LocalProfanityCensor",
    [switch]$SkipOpenVoice
)

$ErrorActionPreference = 'Stop'

$runtimeRoot = Join-Path $InstallRoot 'runtime'
$venvRoot = Join-Path $runtimeRoot 'venv'
$modelsRoot = Join-Path $InstallRoot 'models'
$openVoiceRoot = Join-Path $modelsRoot 'openvoice'

New-Item -ItemType Directory -Force -Path $runtimeRoot, $modelsRoot, $openVoiceRoot | Out-Null

Write-Host "Creating AI runtime under $runtimeRoot"
& $PythonPath -m venv $venvRoot

$venvPython = Join-Path $venvRoot 'Scripts\python.exe'
& $venvPython -m pip install --upgrade pip
& $venvPython -m pip install faster-whisper demucs pyyaml

if (-not $SkipOpenVoice) {
    & $venvPython -m pip install openvoice-cli melo-tts
}

Write-Host "AI bootstrap completed."
Write-Host "Set CENSOR_MEDIA_PYTHON=$venvPython"
if (-not $SkipOpenVoice) {
    Write-Host "Set CENSOR_OPENVOICE_CHECKPOINTS to your OpenVoice checkpoints folder."
}