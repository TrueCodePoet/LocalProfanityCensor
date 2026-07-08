[CmdletBinding()]
param(
    [ValidateSet('cpu','gpu')]
    [string]$Profile = 'gpu',
    [string]$PythonPath = 'python',
    [string]$InstallRoot = "$env:ProgramData\LocalProfanityCensor",
    [switch]$SkipModelDownload,
    [string]$ModelName = 'large-v3'
)

$ErrorActionPreference = 'Stop'

$BundleRoot = Split-Path -Parent $PSCommandPath
$ScriptsRoot = Join-Path $BundleRoot 'Scripts'
$setupScript = Join-Path $ScriptsRoot 'Setup-AI.ps1'
$downloadModelScript = Join-Path $ScriptsRoot 'Download-FasterWhisperModel.ps1'

if (-not (Test-Path -LiteralPath $setupScript)) {
    throw "Bundle setup script not found: $setupScript. Run Build-Bundle.ps1 first."
}

& $setupScript -Profile $Profile -PythonPath $PythonPath -InstallRoot $InstallRoot -SkipOpenVoice

if (-not $SkipModelDownload) {
    if (-not (Test-Path -LiteralPath $downloadModelScript)) {
        throw "Bundle model download script not found: $downloadModelScript. Run Build-Bundle.ps1 first."
    }

    & $downloadModelScript -ModelName $ModelName
}

Write-Host 'Bundle setup complete.'