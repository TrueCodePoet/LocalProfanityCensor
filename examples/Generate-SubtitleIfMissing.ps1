[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InputPath,

    [string]$OutputPath,

    [string]$ConfigPath,
    [switch]$KeepWork
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ProjectPath = Join-Path $RepoRoot 'src\LocalProfanityCensor.DotNet\LocalProfanityCensor.DotNet.csproj'
$DictionaryPath = Join-Path $RepoRoot 'src\LocalProfanityCensor.DotNet\Dictionaries\profanity.example.yml'

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $RepoRoot 'src\LocalProfanityCensor.DotNet\Configs\subtitle-only.if-missing.medium.yml'
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $inputItem = Get-Item -LiteralPath $InputPath
    $OutputPath = Join-Path $inputItem.DirectoryName ("{0}.subtitles.mkv" -f [System.IO.Path]::GetFileNameWithoutExtension($inputItem.Name))
}

$arguments = @(
    'run', '--project', $ProjectPath, '--',
    'process-file', $InputPath, $OutputPath,
    '--dictionary', $DictionaryPath,
    '--config', $ConfigPath,
    '--mode', 'mute'
)

if ($KeepWork) { $arguments += '--keep-work' }

dotnet @arguments