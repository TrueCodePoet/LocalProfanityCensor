[CmdletBinding()]
param(
    [string]$ModelName = 'large-v3',
    [string]$PythonPath = $env:CENSOR_MEDIA_PYTHON,
    [string]$CacheRoot = $env:CENSOR_MEDIA_HF_HOME
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($PythonPath)) {
    $PythonPath = 'python'
}

if ([string]::IsNullOrWhiteSpace($CacheRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
    $CacheRoot = Join-Path $RepoRoot 'models\huggingface-cache'
}

New-Item -ItemType Directory -Force -Path $CacheRoot | Out-Null

$env:CENSOR_MEDIA_HF_HOME = [System.IO.Path]::GetFullPath($CacheRoot)
$env:HF_HOME = $env:CENSOR_MEDIA_HF_HOME
$env:HUGGINGFACE_HUB_CACHE = Join-Path $env:CENSOR_MEDIA_HF_HOME 'hub'
$env:TRANSFORMERS_CACHE = Join-Path $env:CENSOR_MEDIA_HF_HOME 'transformers'
$env:HF_HUB_DISABLE_IMPLICIT_TOKEN = '1'
$env:HF_HUB_DISABLE_SYMLINKS_WARNING = '1'

$code = @'
import os
import sys
from faster_whisper.utils import download_model

model_name = sys.argv[1]
cache_dir = os.environ.get("HUGGINGFACE_HUB_CACHE") or None
model_path = download_model(model_name, local_files_only=False, cache_dir=cache_dir)
print(model_path)
'@

Write-Host "Downloading faster-whisper model '$ModelName' into $env:CENSOR_MEDIA_HF_HOME"
& $PythonPath -c $code $ModelName

if ($LASTEXITCODE -ne 0) {
    throw "Model download failed for '$ModelName'. Ensure Python can import faster_whisper."
}