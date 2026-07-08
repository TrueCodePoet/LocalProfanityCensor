[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InputPath,

    [string]$OutputPath,

    [ValidateSet('mute', 'beep', 'duck', 'replace')]
    [string]$Mode = 'mute',

    [string]$ConfigPath,
    [string]$DictionaryPath,

    [switch]$DryRun,
    [switch]$KeepWork
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ProjectPath = Join-Path $RepoRoot 'src\LocalProfanityCensor.DotNet\LocalProfanityCensor.DotNet.csproj'

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $RepoRoot 'src\LocalProfanityCensor.DotNet\Configs\production.demucs.depth.censored-option.yml'
}

if ([string]::IsNullOrWhiteSpace($DictionaryPath)) {
    $DictionaryPath = Join-Path $RepoRoot 'src\LocalProfanityCensor.DotNet\Dictionaries\profanity.example.yml'
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $inputItem = Get-Item -LiteralPath $InputPath
    $OutputPath = Join-Path $inputItem.DirectoryName ("{0}.clean.mkv" -f [System.IO.Path]::GetFileNameWithoutExtension($inputItem.Name))
}

$arguments = @(
    'run', '--project', $ProjectPath, '--',
    'process-file', $InputPath, $OutputPath,
    '--dictionary', $DictionaryPath,
    '--config', $ConfigPath,
    '--mode', $Mode
)

if ($DryRun) { $arguments += '--dry-run' }
if ($KeepWork) { $arguments += '--keep-work' }

dotnet @arguments