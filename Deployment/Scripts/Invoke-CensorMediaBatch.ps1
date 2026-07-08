[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InputRoot,

    [string]$HoldingRoot,
    [string]$StateRoot,
    [string]$ArchiveRoot,

    [string]$PayloadSource,
    [string]$LocalInstallRoot = "$env:ProgramData\LocalProfanityCensor\Current",
    [string]$LocalWorkRoot = (Join-Path $env:TEMP 'LocalProfanityCensorBatch'),

    [string]$CensorExe,
    [string]$ConfigPath,
    [string]$DictionaryPath,

    [switch]$ReplaceOriginal,
    [switch]$Repeat,
    [int]$RepeatIntervalMinutes = 15,
    [int]$MaxFilesPerRun = 0,

    [int]$MaxAttempts = 3,
    [int]$RetryDelayMinutes = 360,
    [double]$LockStaleHours = 12,

    [switch]$DryRun,
    [switch]$KeepWork,
    [switch]$Bootstrap,
    [switch]$AllowPrompt,
    [switch]$SkipLocalInstall,
    [switch]$RunSetupAI,
    [switch]$SkipModelPreflight,

    [ValidateSet('cpu', 'gpu')]
    [string]$AIProfile = 'gpu',
    [string]$PythonPath = 'python',
    [string]$MediaPythonPath,
    [string]$HuggingFaceCacheRoot,

    [string[]]$MediaExtensions = @('*.mkv', '*.mp4', '*.m4v', '*.avi', '*.m2ts')
)

$ErrorActionPreference = 'Stop'

$ScriptRoot = Split-Path -Parent $PSCommandPath
$DeploymentRoot = Split-Path -Parent $ScriptRoot

function Write-Status {
    param([string]$Message)
    Write-Host $Message
}

function Get-FullPathSafe {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $Path
    }

    return [System.IO.Path]::GetFullPath($Path)
}

function Join-OptionalPath {
    param(
        [string]$Root,
        [string]$Child
    )

    if ([string]::IsNullOrWhiteSpace($Child)) {
        return $Root
    }

    return Join-Path $Root $Child
}

function Get-RelativePathSafe {
    param(
        [string]$Root,
        [string]$Path
    )

    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $pathFull = [System.IO.Path]::GetFullPath($Path)
    $rootUri = [Uri]$rootFull
    $pathUri = [Uri]$pathFull

    if ($rootUri.Scheme -ne $pathUri.Scheme) {
        return [System.IO.Path]::GetFileName($pathFull)
    }

    $relative = [Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString())
    return $relative.Replace('/', '\')
}

function Test-IsUnderPath {
    param(
        [string]$Path,
        [string]$Root
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Root)) {
        return $false
    }

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')

    return $fullPath.Equals($fullRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $fullPath.StartsWith($fullRoot + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Get-StableId {
    param([string]$Text)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text.ToLowerInvariant())
        $hash = $sha.ComputeHash($bytes)
        return -join ($hash | ForEach-Object { $_.ToString('x2') })
    }
    finally {
        $sha.Dispose()
    }
}

function ConvertTo-ProcessArgumentString {
    param([string[]]$Arguments)

    $escapedArguments = foreach ($argument in $Arguments) {
        if ($null -eq $argument) {
            '""'
            continue
        }

        $text = [string]$argument
        if ($text.Length -eq 0) {
            '""'
            continue
        }

        if ($text -notmatch '[\s"]') {
            $text
            continue
        }

        '"' + (($text -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"'
    }

    return ($escapedArguments -join ' ')
}

function Get-DirectoryStamp {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    $files = @(Get-ChildItem -LiteralPath $Path -File -Recurse -ErrorAction Stop)
    $maxTicks = 0L
    foreach ($file in $files) {
        if ($file.LastWriteTimeUtc.Ticks -gt $maxTicks) {
            $maxTicks = $file.LastWriteTimeUtc.Ticks
        }
    }

    return [PSCustomObject]@{
        FileCount = $files.Count
        MaxLastWriteTimeUtcTicks = $maxTicks
    }
}

function Read-JsonFile {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        $corruptPath = "$Path.corrupt.$(Get-Date -Format yyyyMMddHHmmss)"
        Move-Item -LiteralPath $Path -Destination $corruptPath -ErrorAction SilentlyContinue
        Write-Warning "State file was unreadable and was moved aside: $corruptPath"
        return $null
    }
}

function Write-JsonFileAtomic {
    param(
        [string]$Path,
        [object]$Value,
        [int]$Depth = 12
    )

    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $tempPath = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    $json = $Value | ConvertTo-Json -Depth $Depth
    Set-Content -LiteralPath $tempPath -Value $json -Encoding UTF8
    Move-Item -LiteralPath $tempPath -Destination $Path -Force
}

function Get-JobStatePath {
    param(
        [string]$StateRoot,
        [string]$JobId
    )

    $bucket = $JobId.Substring(0, 2)
    return Join-Path (Join-Path $StateRoot "jobs\$bucket") "$JobId.json"
}

function Get-JobLockPath {
    param(
        [string]$StateRoot,
        [string]$JobId
    )

    return Join-Path (Join-Path $StateRoot 'locks') "$JobId.lock"
}

function Acquire-JobLock {
    param(
        [string]$LockPath,
        [double]$StaleHours,
        [string]$SourcePath
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LockPath) | Out-Null

    if (Test-Path -LiteralPath $LockPath) {
        try {
            $lockAge = (Get-Date) - (Get-Item -LiteralPath $LockPath).LastWriteTime
            if ($lockAge.TotalHours -ge $StaleHours) {
                Remove-Item -LiteralPath $LockPath -Force -ErrorAction SilentlyContinue
            }
        }
        catch {
        }
    }

    try {
        $stream = [System.IO.File]::Open($LockPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        $writer = New-Object System.IO.StreamWriter($stream)
        $writer.WriteLine("Machine=$env:COMPUTERNAME")
        $writer.WriteLine("PID=$PID")
        $writer.WriteLine("Started=$(Get-Date -Format o)")
        $writer.WriteLine("Source=$SourcePath")
        $writer.Flush()

        return [PSCustomObject]@{
            Path = $LockPath
            Stream = $stream
            Writer = $writer
        }
    }
    catch {
        return $null
    }
}

function Update-JobLockHeartbeat {
    param($LockInfo)

    if ($null -eq $LockInfo -or [string]::IsNullOrWhiteSpace([string]$LockInfo.Path)) {
        return
    }

    try {
        [System.IO.File]::SetLastWriteTimeUtc($LockInfo.Path, [DateTime]::UtcNow)
    }
    catch {
    }
}

function Release-JobLock {
    param($LockInfo)

    if ($null -eq $LockInfo) {
        return
    }

    try {
        if ($LockInfo.Writer) { $LockInfo.Writer.Dispose() }
    }
    catch {
    }

    try {
        if ($LockInfo.Stream) { $LockInfo.Stream.Dispose() }
    }
    catch {
    }

    Remove-Item -LiteralPath $LockInfo.Path -Force -ErrorAction SilentlyContinue
}

function Invoke-ExternalProcess {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$WorkingDirectory,
        [string]$StdOutPath,
        [string]$StdErrPath,
        $LockInfo
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $StdOutPath), (Split-Path -Parent $StdErrPath) | Out-Null
    $argumentString = ConvertTo-ProcessArgumentString -Arguments $Arguments

    $process = Start-Process -FilePath $FilePath `
                             -ArgumentList $argumentString `
                             -WorkingDirectory $WorkingDirectory `
                             -NoNewWindow `
                             -PassThru `
                             -RedirectStandardOutput $StdOutPath `
                             -RedirectStandardError $StdErrPath

    while (-not $process.HasExited) {
        Update-JobLockHeartbeat -LockInfo $LockInfo
        Start-Sleep -Seconds 5
        $process.Refresh()
    }

    $process.WaitForExit()
    Update-JobLockHeartbeat -LockInfo $LockInfo

    return [PSCustomObject]@{
        ExitCode = $process.ExitCode
        StdOutPath = $StdOutPath
        StdErrPath = $StdErrPath
    }
}

function Sync-LocalPayload {
    param(
        [string]$Source,
        [string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        return $false
    }

    $sourceStamp = Get-DirectoryStamp -Path $Source
    $stampPath = Join-Path $Destination 'deployment-source.json'
    $currentStamp = Read-JsonFile -Path $stampPath
    $needsCopy = -not (Test-Path -LiteralPath (Join-Path $Destination 'LocalProfanityCensor.DotNet.exe'))

    if (-not $needsCopy -and $null -ne $sourceStamp -and $null -ne $currentStamp) {
        $needsCopy = [int64]$currentStamp.SourceStamp.MaxLastWriteTimeUtcTicks -ne [int64]$sourceStamp.MaxLastWriteTimeUtcTicks -or
            [int]$currentStamp.SourceStamp.FileCount -ne [int]$sourceStamp.FileCount
    }

    if ($needsCopy) {
        Write-Status "Syncing LocalProfanityCensor payload to $Destination"
        New-Item -ItemType Directory -Force -Path $Destination | Out-Null
        Copy-Item -Path (Join-Path $Source '*') -Destination $Destination -Recurse -Force
        Write-JsonFileAtomic -Path $stampPath -Value ([PSCustomObject]@{
            Source = $Source
            SyncedAt = (Get-Date).ToString('o')
            SourceStamp = $sourceStamp
        })
    }

    return $true
}

function Resolve-CensorRuntime {
    param(
        [string]$ExplicitExe,
        [string]$ExplicitPayloadSource,
        [string]$InstallRoot,
        [switch]$SkipInstall
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitExe)) {
        if (-not (Test-Path -LiteralPath $ExplicitExe)) {
            throw "Censor executable was not found: $ExplicitExe"
        }
        return [System.IO.Path]::GetFullPath($ExplicitExe)
    }

    if (-not $SkipInstall) {
        $source = $ExplicitPayloadSource
        if ([string]::IsNullOrWhiteSpace($source)) {
            $source = Join-Path $DeploymentRoot 'artifacts\staging\LocalProfanityCensor'
        }

        [void](Sync-LocalPayload -Source $source -Destination $InstallRoot)
    }

    $localExe = Join-Path $InstallRoot 'LocalProfanityCensor.DotNet.exe'
    if (Test-Path -LiteralPath $localExe) {
        return $localExe
    }

    throw "LocalProfanityCensor executable was not found. Run Deployment\Scripts\Publish-Core.ps1 and Stage-Payload.ps1, or pass -CensorExe."
}

function Initialize-AIRuntimeIfRequested {
    param(
        [string]$InstallRoot,
        [string]$Profile,
        [string]$PythonPath
    )

    if (-not $RunSetupAI) {
        return
    }

    $setupScript = Join-Path $ScriptRoot 'Setup-AI.ps1'
    if (-not (Test-Path -LiteralPath $setupScript)) {
        throw "Setup-AI.ps1 was not found: $setupScript"
    }

    $machineRoot = Split-Path -Parent $InstallRoot
    & $setupScript -Profile $Profile -PythonPath $PythonPath -InstallRoot $machineRoot -SkipOpenVoice
}

function Initialize-CensorEnvironment {
    param(
        [string]$InstallRoot,
        [string]$MediaPythonPath,
        [string]$HuggingFaceCacheRoot
    )

    $machineRoot = Split-Path -Parent $InstallRoot
    $venvPython = Join-Path $machineRoot 'runtime\venv\Scripts\python.exe'
    if (-not [string]::IsNullOrWhiteSpace($MediaPythonPath)) {
        if (-not (Test-Path -LiteralPath $MediaPythonPath)) {
            throw "Media Python runtime was not found: $MediaPythonPath"
        }
        $env:CENSOR_MEDIA_PYTHON = [System.IO.Path]::GetFullPath($MediaPythonPath)
    } elseif ([string]::IsNullOrWhiteSpace($env:CENSOR_MEDIA_PYTHON) -and (Test-Path -LiteralPath $venvPython)) {
        $env:CENSOR_MEDIA_PYTHON = $venvPython
    }

    if (-not [string]::IsNullOrWhiteSpace($HuggingFaceCacheRoot)) {
        $hfHome = [System.IO.Path]::GetFullPath($HuggingFaceCacheRoot)
    } elseif (-not [string]::IsNullOrWhiteSpace($env:CENSOR_MEDIA_HF_HOME)) {
        $hfHome = [System.IO.Path]::GetFullPath($env:CENSOR_MEDIA_HF_HOME)
    } else {
        $hfHome = Join-Path $machineRoot 'hf-home'
    }

    New-Item -ItemType Directory -Force -Path $hfHome | Out-Null
    $env:CENSOR_MEDIA_HF_HOME = $hfHome
    $env:HF_HOME = $hfHome
    $env:HUGGINGFACE_HUB_CACHE = Join-Path $hfHome 'hub'
    $env:TRANSFORMERS_CACHE = Join-Path $hfHome 'transformers'
    if ([string]::IsNullOrWhiteSpace($env:HF_HUB_DISABLE_IMPLICIT_TOKEN)) { $env:HF_HUB_DISABLE_IMPLICIT_TOKEN = '1' }
    if ([string]::IsNullOrWhiteSpace($env:HF_HUB_DISABLE_SYMLINKS_WARNING)) { $env:HF_HUB_DISABLE_SYMLINKS_WARNING = '1' }
}

function Resolve-DefaultConfigPath {
    param(
        [string]$ExplicitPath,
        [string]$InstallRoot
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        return [System.IO.Path]::GetFullPath($ExplicitPath)
    }

    $preferred = Join-Path $InstallRoot 'Configs\production.demucs.depth.censored-option.yml'
    if (Test-Path -LiteralPath $preferred) {
        return $preferred
    }

    return Join-Path $InstallRoot 'Configs\production.demucs.depth.medium.yml'
}

function Resolve-DefaultDictionaryPath {
    param(
        [string]$ExplicitPath,
        [string]$InstallRoot
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        return [System.IO.Path]::GetFullPath($ExplicitPath)
    }

    return Join-Path $InstallRoot 'Dictionaries\profanity.example.yml'
}

function Get-ConfiguredAsrModelName {
    param([string]$ConfigPath)

    $inTranscription = $false
    foreach ($line in Get-Content -LiteralPath $ConfigPath) {
        if ($line -match '^transcription:\s*$') {
            $inTranscription = $true
            continue
        }

        if ($inTranscription -and $line -match '^\S') {
            $inTranscription = $false
        }

        if ($inTranscription -and $line -match '^\s*model:\s*(.+?)\s*(#.*)?$') {
            return $matches[1].Trim().Trim('"').Trim("'")
        }
    }

    return 'medium'
}

function Assert-AsrModelAvailable {
    param(
        [string]$ConfigPath,
        [string]$StateRoot
    )

    $modelName = Get-ConfiguredAsrModelName -ConfigPath $ConfigPath
    $pythonPath = [string]$env:CENSOR_MEDIA_PYTHON
    if ([string]::IsNullOrWhiteSpace($pythonPath)) {
        throw "CENSOR_MEDIA_PYTHON is not set. Use -MediaPythonPath or -RunSetupAI before processing."
    }

    if (-not (Test-Path -LiteralPath $pythonPath)) {
        throw "CENSOR_MEDIA_PYTHON does not exist: $pythonPath"
    }

    $preflightRoot = Join-Path $StateRoot '.preflight'
    New-Item -ItemType Directory -Force -Path $preflightRoot | Out-Null
    $stdoutPath = Join-Path $preflightRoot 'asr-model-preflight.stdout.log'
    $stderrPath = Join-Path $preflightRoot 'asr-model-preflight.stderr.log'
    $code = @'
import os
import sys
import demucs.separate  # noqa: F401
from faster_whisper.utils import download_model

model_name = sys.argv[1]
cache_dir = os.environ.get("HUGGINGFACE_HUB_CACHE") or None
model_path = download_model(model_name, local_files_only=True, cache_dir=cache_dir)
print(model_path)
'@

    Write-Status "Preflight  : checking ASR runtime and faster-whisper model '$modelName'"
    $result = Invoke-ExternalProcess -FilePath $pythonPath `
                                     -Arguments @('-c', $code, $modelName) `
                                     -WorkingDirectory $DeploymentRoot `
                                     -StdOutPath $stdoutPath `
                                     -StdErrPath $stderrPath `
                                     -LockInfo $null

    if ($result.ExitCode -ne 0) {
        $stderr = if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath -Raw } else { '' }
        $stdout = if (Test-Path -LiteralPath $stdoutPath) { Get-Content -LiteralPath $stdoutPath -Raw } else { '' }
        throw "ASR model preflight failed for '$modelName'. Cache root: $env:HF_HOME. StdOut: $stdout StdErr: $stderr"
    }

    $resolvedModelPath = if (Test-Path -LiteralPath $stdoutPath) { (Get-Content -LiteralPath $stdoutPath -Raw).Trim() } else { '' }
    Write-Status "Preflight  : ASR model '$modelName' available at $resolvedModelPath"
}

function Get-CandidateFiles {
    param(
        [string]$Root,
        [string[]]$Extensions,
        [string[]]$ExcludedRoots
    )

    $files = foreach ($extension in $Extensions) {
        Get-ChildItem -LiteralPath $Root -Filter $extension -File -Recurse -ErrorAction SilentlyContinue
    }

    return @($files | Where-Object {
        $path = $_.FullName
        $include = $true
        if ($path -match '\\\.work(\\|$)') { $include = $false }
        foreach ($excludedRoot in $ExcludedRoots) {
            if ($include -and (Test-IsUnderPath -Path $path -Root $excludedRoot)) { $include = $false }
        }
        $include
    } | Sort-Object Length -Descending)
}

function Get-SidecarSubtitleFiles {
    param([System.IO.FileInfo]$MediaFile)

    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($MediaFile.Name)
    $escapedBaseName = [regex]::Escape($baseName)
    $directories = @($MediaFile.DirectoryName)
    $subsDir = Join-Path $MediaFile.DirectoryName 'Subs'
    if (Test-Path -LiteralPath $subsDir) {
        $directories += $subsDir
    }

    $sidecars = foreach ($directory in $directories) {
        Get-ChildItem -LiteralPath $directory -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -in @('.srt', '.vtt') -and $_.BaseName -match "^$escapedBaseName($|[\.\-_ ])" }
    }

    if (-not $sidecars) {
        return @()
    }

    return @($sidecars | Sort-Object FullName -Unique)
}

function Copy-SidecarSubtitles {
    param(
        [System.IO.FileInfo[]]$Sidecars,
        [string]$DestinationDirectory
    )

    $copied = @()
    foreach ($sidecar in @($Sidecars)) {
        if ($null -eq $sidecar -or [string]::IsNullOrWhiteSpace([string]$sidecar.FullName)) {
            continue
        }

        $target = Join-Path $DestinationDirectory $sidecar.Name
        if (Test-Path -LiteralPath $target) {
            $target = Join-Path $DestinationDirectory ("{0}.{1}{2}" -f $sidecar.BaseName, (Get-StableId -Text $sidecar.FullName).Substring(0, 8), $sidecar.Extension)
        }

        Copy-Item -LiteralPath $sidecar.FullName -Destination $target -Force
        $copied += Get-Item -LiteralPath $target
    }

    return $copied
}

function Add-SidecarsToLocalMkv {
    param(
        [string]$InputPath,
        [System.IO.FileInfo[]]$Sidecars,
        [string]$OutputPath,
        $LockInfo,
        [string]$LogDirectory
    )

    $validSidecars = @($Sidecars | Where-Object { $null -ne $_ -and -not [string]::IsNullOrWhiteSpace([string]$_.FullName) })
    if ($validSidecars.Count -eq 0) {
        return $InputPath
    }

    $ffmpegCommand = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if (-not $ffmpegCommand) {
        Write-Warning 'ffmpeg was not found in PATH; sidecar subtitles will remain sidecar-only in local work.'
        return $InputPath
    }

    $args = @('-y', '-i', $InputPath)
    foreach ($sidecar in $validSidecars) {
        $args += @('-i', $sidecar.FullName)
    }

    $args += @('-map', '0')
    for ($index = 0; $index -lt $validSidecars.Count; $index++) {
        $args += @('-map', ('{0}:0' -f ($index + 1)))
    }
    $args += @('-c', 'copy', $OutputPath)

    Write-Status "  Sidecars   : embedding $($validSidecars.Count) sidecar subtitle file(s) into local MKV input"
    $result = Invoke-ExternalProcess -FilePath $ffmpegCommand.Source `
                                     -Arguments $args `
                                     -WorkingDirectory (Split-Path -Parent $InputPath) `
                                     -StdOutPath (Join-Path $LogDirectory 'ffmpeg-sidecar.stdout.log') `
                                     -StdErrPath (Join-Path $LogDirectory 'ffmpeg-sidecar.stderr.log') `
                                     -LockInfo $LockInfo

    if ($result.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $OutputPath)) {
        Write-Warning "Sidecar embedding failed with exit code $($result.ExitCode); continuing with copied sidecar files."
        return $InputPath
    }

    return $OutputPath
}

function Get-OutputPathForSource {
    param(
        [System.IO.FileInfo]$File,
        [string]$RelativePath,
        [string]$HoldingRoot,
        [switch]$ReplaceOriginal
    )

    $relativeDirectory = Split-Path -Path $RelativePath -Parent
    $targetName = [System.IO.Path]::ChangeExtension([System.IO.Path]::GetFileName($RelativePath), '.mkv')

    if ($ReplaceOriginal) {
        return Join-Path $File.DirectoryName $targetName
    }

    return Join-OptionalPath -Root (Join-Path $HoldingRoot $relativeDirectory) -Child $targetName
}

function Get-ArchivePathForSource {
    param(
        [string]$ArchiveRoot,
        [string]$RelativePath
    )

    return Join-OptionalPath -Root $ArchiveRoot -Child $RelativePath
}

function Get-AvailablePath {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $Path
    }

    $directory = Split-Path -Parent $Path
    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($Path)
    $extension = [System.IO.Path]::GetExtension($Path)

    for ($index = 1; $index -lt 1000; $index++) {
        $candidate = Join-Path $directory ("{0}.{1:000}{2}" -f $baseName, $index, $extension)
        if (-not (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    throw "Could not find available path for $Path"
}

function Invoke-FfprobeJson {
    param([string]$Path)

    $ffprobeCommand = Get-Command ffprobe -ErrorAction SilentlyContinue
    if (-not $ffprobeCommand) {
        throw 'ffprobe was not found in PATH.'
    }

    $stdoutPath = [System.IO.Path]::GetTempFileName()
    $stderrPath = [System.IO.Path]::GetTempFileName()
    try {
        $result = Invoke-ExternalProcess -FilePath $ffprobeCommand.Source `
                                         -Arguments @('-v', 'error', '-show_format', '-show_streams', '-of', 'json', $Path) `
                                         -WorkingDirectory (Split-Path -Parent $Path) `
                                         -StdOutPath $stdoutPath `
                                         -StdErrPath $stderrPath `
                                         -LockInfo $null
        if ($result.ExitCode -ne 0) {
            throw "ffprobe failed for $Path"
        }

        return Get-Content -LiteralPath $stdoutPath -Raw | ConvertFrom-Json
    }
    finally {
        Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

function Test-OutputMedia {
    param(
        [string]$SourcePath,
        [string]$OutputPath
    )

    if (-not (Test-Path -LiteralPath $OutputPath)) {
        throw "Output file was not created: $OutputPath"
    }

    $sourceProbe = Invoke-FfprobeJson -Path $SourcePath
    $outputProbe = Invoke-FfprobeJson -Path $OutputPath
    $outputStreams = @($outputProbe.streams)
    $videoStreams = @($outputStreams | Where-Object { $_.codec_type -eq 'video' })
    $audioStreams = @($outputStreams | Where-Object { $_.codec_type -eq 'audio' })

    if ($videoStreams.Count -eq 0) {
        throw "Output has no video stream: $OutputPath"
    }

    if ($audioStreams.Count -eq 0) {
        throw "Output has no audio stream: $OutputPath"
    }

    $sourceDuration = 0.0
    $outputDuration = 0.0
    if ($sourceProbe.format.duration) { $sourceDuration = [double]$sourceProbe.format.duration }
    if ($outputProbe.format.duration) { $outputDuration = [double]$outputProbe.format.duration }
    if ($sourceDuration -gt 0 -and $outputDuration -gt 0 -and [math]::Abs($sourceDuration - $outputDuration) -gt 5.0) {
        throw "Output duration differs from source by more than 5 seconds. Source=$sourceDuration Output=$outputDuration"
    }

    $cleanDefaultAudio = @($audioStreams | Where-Object {
        [string]$_.tags.title -eq 'Clean Censored' -and $_.disposition.default -eq 1
    })
    if ($cleanDefaultAudio.Count -gt 0 -and $audioStreams.Count -gt 1) {
        throw 'Clean Censored audio is unexpectedly marked default. Original audio should remain default.'
    }

    return [PSCustomObject]@{
        VideoStreams = $videoStreams.Count
        AudioStreams = $audioStreams.Count
        SubtitleStreams = @($outputStreams | Where-Object { $_.codec_type -eq 'subtitle' }).Count
    }
}

function Get-JobDecision {
    param(
        [System.IO.FileInfo]$File,
        [string]$RelativePath,
        [string]$StatePath,
        [string]$OutputPath,
        [int]$MaxAttempts,
        [int]$RetryDelayMinutes
    )

    $holdingOutputExists = -not $File.FullName.Equals($OutputPath, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $OutputPath)
    if ($holdingOutputExists) {
        return [PSCustomObject]@{ ShouldRun = $false; State = $null; Reason = 'output_exists' }
    }

    $state = Read-JsonFile -Path $StatePath
    if ($null -eq $state) {
        return [PSCustomObject]@{ ShouldRun = $true; State = $null; Reason = 'new' }
    }

    $sourceMatches = Test-SourceMatchesState -State $state -File $File

    if ($sourceMatches -and (Test-Path -LiteralPath $OutputPath)) {
        return [PSCustomObject]@{ ShouldRun = $false; State = $state; Reason = 'output_exists' }
    }

    if ($sourceMatches -and (Test-IsCompletedJobStatus -Status ([string]$state.Status))) {
        if ([string]$state.Status -eq 'completed_no_changes' -or (Test-Path -LiteralPath $OutputPath)) {
            return [PSCustomObject]@{ ShouldRun = $false; State = $state; Reason = [string]$state.Status }
        }
    }

    if ($state.Status -eq 'completed' -and $sourceMatches -and (Test-Path -LiteralPath $OutputPath)) {
        return [PSCustomObject]@{ ShouldRun = $false; State = $state; Reason = 'completed' }
    }

    $attempts = if ($state.Attempts) { [int]$state.Attempts } else { 0 }
    if ($attempts -ge $MaxAttempts -and $sourceMatches -and $state.Status -eq 'failed') {
        return [PSCustomObject]@{ ShouldRun = $false; State = $state; Reason = 'max_attempts' }
    }

    if ($state.Status -eq 'failed' -and $sourceMatches -and $state.CompletedAt) {
        $lastCompleted = [DateTime]::Parse([string]$state.CompletedAt)
        if (((Get-Date) - $lastCompleted).TotalMinutes -lt $RetryDelayMinutes) {
            return [PSCustomObject]@{ ShouldRun = $false; State = $state; Reason = 'retry_delay' }
        }
    }

    return [PSCustomObject]@{ ShouldRun = $true; State = $state; Reason = 'retry_or_changed' }
}

function Test-SourceMatchesState {
    param(
        $State,
        [System.IO.FileInfo]$File
    )

    if ($null -eq $State -or $null -eq $State.Source) {
        return $false
    }

    if ([int64]$State.Source.Length -ne [int64]$File.Length) {
        return $false
    }

    $stateTimestamp = $State.Source.LastWriteTimeUtc
    if ($stateTimestamp -is [DateTime]) {
        return [Math]::Abs(($stateTimestamp.ToUniversalTime() - $File.LastWriteTimeUtc).TotalMilliseconds) -lt 1
    }

    $parsedTimestamp = [DateTimeOffset]::MinValue
    if ([DateTimeOffset]::TryParse([string]$stateTimestamp, [ref]$parsedTimestamp)) {
        return [Math]::Abs(($parsedTimestamp.UtcDateTime - $File.LastWriteTimeUtc).TotalMilliseconds) -lt 1
    }

    return [string]$stateTimestamp -eq $File.LastWriteTimeUtc.ToString('o')
}

function Test-IsCompletedJobStatus {
    param([string]$Status)

    return $Status -in @('completed', 'completed_with_warnings', 'completed_subtitle_only', 'completed_no_changes')
}

function Read-CensorProcessResult {
    param([string]$StdOutPath)

    if ([string]::IsNullOrWhiteSpace($StdOutPath) -or -not (Test-Path -LiteralPath $StdOutPath)) {
        return $null
    }

    $text = Get-Content -LiteralPath $StdOutPath -Raw
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }

    try {
        return $text | ConvertFrom-Json
    }
    catch {
        Write-Warning "Unable to parse LocalProfanityCensor JSON result from '$StdOutPath': $($_.Exception.Message)"
        return $null
    }
}

function Move-CompanionReports {
    param(
        [string]$LocalOutputPath,
        [string]$FinalOutputPath
    )

    $localDirectory = Split-Path -Parent $LocalOutputPath
    $localBase = [System.IO.Path]::GetFileNameWithoutExtension($LocalOutputPath)
    $finalDirectory = Split-Path -Parent $FinalOutputPath
    $finalBase = [System.IO.Path]::GetFileNameWithoutExtension($FinalOutputPath)
    $moved = @()

    foreach ($report in @(Get-ChildItem -LiteralPath $localDirectory -Filter "$localBase.report.*" -File -ErrorAction SilentlyContinue)) {
        $targetName = $report.Name -replace ('^' + [regex]::Escape($localBase)), $finalBase
        $targetPath = Join-Path $finalDirectory $targetName
        Move-Item -LiteralPath $report.FullName -Destination $targetPath -ErrorAction Stop
        $moved += $targetPath
    }

    return $moved
}

function Process-CensorJob {
    param(
        [System.IO.FileInfo]$File,
        [string]$RelativePath,
        [string]$JobId,
        [string]$StatePath,
        $PriorState,
        $LockInfo,
        [string]$OutputPath,
        [string]$ArchivePath,
        [string]$CensorExePath,
        [string]$ConfigPath,
        [string]$DictionaryPath
    )

    $attempt = 1
    if ($PriorState -and $PriorState.Attempts) {
        $attempt = [int]$PriorState.Attempts + 1
    }

    $jobWorkRoot = Join-Path $LocalWorkRoot $JobId
    $inputDirectory = Join-Path $jobWorkRoot 'input'
    $outputDirectory = Join-Path $jobWorkRoot 'output'
    $logDirectory = Join-Path $jobWorkRoot 'logs'
    $jobSucceeded = $false
    New-Item -ItemType Directory -Force -Path $inputDirectory, $outputDirectory, $logDirectory | Out-Null

    $sourceInfo = [PSCustomObject]@{
        Path = $File.FullName
        RelativePath = $RelativePath
        Length = $File.Length
        LastWriteTimeUtc = $File.LastWriteTimeUtc.ToString('o')
    }

    $runningState = [PSCustomObject]@{
        SchemaVersion = 1
        JobId = $JobId
        Status = 'running'
        Attempts = $attempt
        StartedAt = (Get-Date).ToString('o')
        CompletedAt = $null
        Machine = $env:COMPUTERNAME
        PID = $PID
        Source = $sourceInfo
        OutputPath = $OutputPath
        ArchivePath = if ($ReplaceOriginal) { $ArchivePath } else { $null }
        LastError = $null
        Logs = @()
        Reports = @()
        Validation = $null
    }
    Write-JsonFileAtomic -Path $StatePath -Value $runningState

    try {
        Write-Status "========================================"
        Write-Status "Input      : $($File.FullName)"
        Write-Status "Output     : $OutputPath"
        Write-Status "Job        : $JobId attempt $attempt/$MaxAttempts on $env:COMPUTERNAME"

        $localInputPath = Join-Path $inputDirectory $File.Name
        Copy-Item -LiteralPath $File.FullName -Destination $localInputPath -Force

        $sidecars = Get-SidecarSubtitleFiles -MediaFile $File
        $copiedSidecars = Copy-SidecarSubtitles -Sidecars $sidecars -DestinationDirectory $inputDirectory
        $processingInputPath = Add-SidecarsToLocalMkv -InputPath $localInputPath `
                                                       -Sidecars $copiedSidecars `
                                                       -OutputPath (Join-Path $inputDirectory ([System.IO.Path]::ChangeExtension(([System.IO.Path]::GetFileNameWithoutExtension($File.Name) + '.with-sidecars'), '.mkv'))) `
                                                       -LockInfo $LockInfo `
                                                       -LogDirectory $logDirectory

        $targetFileName = [System.IO.Path]::GetFileName($OutputPath)
        $localOutputPath = Join-Path $outputDirectory $targetFileName
        $censorArgs = @('process-file', $processingInputPath, $localOutputPath, '--dictionary', $DictionaryPath, '--config', $ConfigPath, '--mode', 'mute', '--progress')
        if ($DryRun) { $censorArgs += '--dry-run' }
        if ($KeepWork) { $censorArgs += '--keep-work' }
        if ($Bootstrap) { $censorArgs += '--bootstrap' }
        if (-not $AllowPrompt) { $censorArgs += '--no-prompt' }

        $processFile = $CensorExePath
        $processArgs = $censorArgs
        if ([System.IO.Path]::GetExtension($CensorExePath).Equals('.dll', [StringComparison]::OrdinalIgnoreCase)) {
            $processFile = 'dotnet'
            $processArgs = @($CensorExePath) + $censorArgs
        }

        $result = Invoke-ExternalProcess -FilePath $processFile `
                                         -Arguments $processArgs `
                                         -WorkingDirectory (Split-Path -Parent $CensorExePath) `
                                         -StdOutPath (Join-Path $logDirectory 'censor.stdout.log') `
                                         -StdErrPath (Join-Path $logDirectory 'censor.stderr.log') `
                                         -LockInfo $LockInfo

        if ($result.ExitCode -ne 0) {
            throw "LocalProfanityCensor exited with code $($result.ExitCode). See $($result.StdErrPath)"
        }

        $censorResult = Read-CensorProcessResult -StdOutPath $result.StdOutPath
        $censorStatus = if ($censorResult -and $censorResult.status) { [string]$censorResult.status } else { 'completed' }
        $noOutputExpected = $censorStatus -eq 'completed_no_changes'

        $validation = $null
        if (-not $DryRun) {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null

            if ($noOutputExpected) {
                $validation = [PSCustomObject]@{
                    NoOutputExpected = $true
                    Reason = 'No censor ranges and no generated subtitle tracks were needed.'
                }
            } else {
                $validation = Test-OutputMedia -SourcePath $processingInputPath -OutputPath $localOutputPath
            }

            if ($noOutputExpected) {
                $reports = Move-CompanionReports -LocalOutputPath $localOutputPath -FinalOutputPath $OutputPath
            } elseif ($ReplaceOriginal) {
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ArchivePath) | Out-Null
                $resolvedArchivePath = Get-AvailablePath -Path $ArchivePath

                if ((Test-Path -LiteralPath $OutputPath) -and -not $File.FullName.Equals($OutputPath, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Replacement target already exists and is not the source file: $OutputPath"
                }

                Move-Item -LiteralPath $File.FullName -Destination $resolvedArchivePath -ErrorAction Stop
                Move-Item -LiteralPath $localOutputPath -Destination $OutputPath -ErrorAction Stop
                $ArchivePath = $resolvedArchivePath
            } else {
                if (Test-Path -LiteralPath $OutputPath) {
                    throw "Holding output already exists: $OutputPath"
                }
                Move-Item -LiteralPath $localOutputPath -Destination $OutputPath -ErrorAction Stop
                $reports = Move-CompanionReports -LocalOutputPath $localOutputPath -FinalOutputPath $OutputPath
            }
        } else {
            $reports = @()
        }

        $completedState = [PSCustomObject]@{
            SchemaVersion = 1
            JobId = $JobId
            Status = if ($DryRun) { 'dry_run_completed' } else { $censorStatus }
            Attempts = $attempt
            StartedAt = $runningState.StartedAt
            CompletedAt = (Get-Date).ToString('o')
            Machine = $env:COMPUTERNAME
            PID = $PID
            Source = $sourceInfo
            OutputPath = if ($DryRun) { $localOutputPath } elseif ($noOutputExpected) { $null } else { $OutputPath }
            ArchivePath = if ($ReplaceOriginal) { $ArchivePath } else { $null }
            LastError = $null
            Logs = @($result.StdOutPath, $result.StdErrPath)
            Reports = @($reports)
            Validation = $validation
        }
        Write-JsonFileAtomic -Path $StatePath -Value $completedState
        if ($noOutputExpected) {
            Write-Status "Done       : no output media needed"
        } else {
            Write-Status "Done       : $OutputPath"
        }
        $jobSucceeded = $true
    }
    catch {
        $errorDetails = [PSCustomObject]@{
            Message = [string]$_.Exception.Message
            Category = [string]$_.CategoryInfo
            ScriptStackTrace = [string]$_.ScriptStackTrace
            PositionMessage = [string]$_.InvocationInfo.PositionMessage
        }
        $failedState = [PSCustomObject]@{
            SchemaVersion = 1
            JobId = $JobId
            Status = 'failed'
            Attempts = $attempt
            StartedAt = $runningState.StartedAt
            CompletedAt = (Get-Date).ToString('o')
            Machine = $env:COMPUTERNAME
            PID = $PID
            Source = $sourceInfo
            OutputPath = $OutputPath
            ArchivePath = if ($ReplaceOriginal) { $ArchivePath } else { $null }
            LastError = [string]$_.Exception.Message
            ErrorDetails = $errorDetails
            Logs = @(
                (Join-Path $logDirectory 'censor.stdout.log'),
                (Join-Path $logDirectory 'censor.stderr.log')
            )
            Reports = @()
            Validation = $null
            WorkRoot = $jobWorkRoot
        }
        Write-JsonFileAtomic -Path $StatePath -Value $failedState
        Write-Warning "Failed     : $($File.FullName) - $($_.Exception.Message)"
    }
    finally {
        if ($jobSucceeded -and -not $KeepWork -and (Test-Path -LiteralPath $jobWorkRoot)) {
            Remove-Item -LiteralPath $jobWorkRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Start-CensorBatchPass {
    param(
        [string]$InputRoot,
        [string]$HoldingRoot,
        [string]$StateRoot,
        [string]$ArchiveRoot,
        [string]$CensorExePath,
        [string]$ConfigPath,
        [string]$DictionaryPath
    )

    $excludedRoots = @($HoldingRoot, $StateRoot, $ArchiveRoot, $LocalWorkRoot) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $candidates = Get-CandidateFiles -Root $InputRoot -Extensions $MediaExtensions -ExcludedRoots $excludedRoots
    Write-Status "Found $($candidates.Count) candidate media file(s) under $InputRoot"

    $processed = 0
    foreach ($file in $candidates) {
        if ($MaxFilesPerRun -gt 0 -and $processed -ge $MaxFilesPerRun) {
            break
        }

        $relativePath = Get-RelativePathSafe -Root $InputRoot -Path $file.FullName
        $jobId = Get-StableId -Text $relativePath
        $statePath = Get-JobStatePath -StateRoot $StateRoot -JobId $jobId
        $outputPath = Get-OutputPathForSource -File $file -RelativePath $relativePath -HoldingRoot $HoldingRoot -ReplaceOriginal:$ReplaceOriginal
        $archivePath = Get-ArchivePathForSource -ArchiveRoot $ArchiveRoot -RelativePath $relativePath
        $decision = Get-JobDecision -File $file -RelativePath $relativePath -StatePath $statePath -OutputPath $outputPath -MaxAttempts $MaxAttempts -RetryDelayMinutes $RetryDelayMinutes

        if (-not $decision.ShouldRun) {
            continue
        }

        $lockPath = Get-JobLockPath -StateRoot $StateRoot -JobId $jobId
        $lockInfo = Acquire-JobLock -LockPath $lockPath -StaleHours $LockStaleHours -SourcePath $file.FullName
        if ($null -eq $lockInfo) {
            continue
        }

        try {
            Process-CensorJob -File $file `
                              -RelativePath $relativePath `
                              -JobId $jobId `
                              -StatePath $statePath `
                              -PriorState $decision.State `
                              -LockInfo $lockInfo `
                              -OutputPath $outputPath `
                              -ArchivePath $archivePath `
                              -CensorExePath $CensorExePath `
                              -ConfigPath $ConfigPath `
                              -DictionaryPath $DictionaryPath
            $processed++
        }
        finally {
            Release-JobLock -LockInfo $lockInfo
        }
    }

    Write-Status "Handled $processed file(s) this pass."
    return $processed
}

$InputRoot = Get-FullPathSafe $InputRoot
if (-not (Test-Path -LiteralPath $InputRoot)) {
    throw "Input root was not found: $InputRoot"
}

if ([string]::IsNullOrWhiteSpace($HoldingRoot)) {
    $HoldingRoot = Join-Path (Split-Path -Parent $InputRoot) '_profanity-censor-holding'
}
if ([string]::IsNullOrWhiteSpace($StateRoot)) {
    $StateRoot = Join-Path $HoldingRoot '.state'
}
if ([string]::IsNullOrWhiteSpace($ArchiveRoot)) {
    $ArchiveRoot = Join-Path $HoldingRoot '_originals'
}

$HoldingRoot = Get-FullPathSafe $HoldingRoot
$StateRoot = Get-FullPathSafe $StateRoot
$ArchiveRoot = Get-FullPathSafe $ArchiveRoot
$LocalWorkRoot = Get-FullPathSafe $LocalWorkRoot
$LocalInstallRoot = Get-FullPathSafe $LocalInstallRoot

New-Item -ItemType Directory -Force -Path $HoldingRoot, $StateRoot, $ArchiveRoot, $LocalWorkRoot | Out-Null

$resolvedCensorExe = Resolve-CensorRuntime -ExplicitExe $CensorExe -ExplicitPayloadSource $PayloadSource -InstallRoot $LocalInstallRoot -SkipInstall:$SkipLocalInstall
Initialize-AIRuntimeIfRequested -InstallRoot $LocalInstallRoot -Profile $AIProfile -PythonPath $PythonPath
Initialize-CensorEnvironment -InstallRoot $LocalInstallRoot -MediaPythonPath $MediaPythonPath -HuggingFaceCacheRoot $HuggingFaceCacheRoot

$resolvedConfigPath = Resolve-DefaultConfigPath -ExplicitPath $ConfigPath -InstallRoot $LocalInstallRoot
$resolvedDictionaryPath = Resolve-DefaultDictionaryPath -ExplicitPath $DictionaryPath -InstallRoot $LocalInstallRoot

if (-not (Test-Path -LiteralPath $resolvedConfigPath)) { throw "Config not found: $resolvedConfigPath" }
if (-not (Test-Path -LiteralPath $resolvedDictionaryPath)) { throw "Dictionary not found: $resolvedDictionaryPath" }

if (-not $SkipModelPreflight) {
    Assert-AsrModelAvailable -ConfigPath $resolvedConfigPath -StateRoot $StateRoot
}

Write-Status "LocalProfanityCensor batch runner"
Write-Status "Input root : $InputRoot"
Write-Status "Holding    : $HoldingRoot"
Write-Status "State      : $StateRoot"
Write-Status "Archive    : $ArchiveRoot"
Write-Status "Local app  : $resolvedCensorExe"
Write-Status "Config     : $resolvedConfigPath"
Write-Status "Dictionary : $resolvedDictionaryPath"
Write-Status "Mode       : $(if ($ReplaceOriginal) { 'replace original after validation' } else { 'holding output only' })"

do {
    [void](Start-CensorBatchPass -InputRoot $InputRoot `
                                  -HoldingRoot $HoldingRoot `
                                  -StateRoot $StateRoot `
                                  -ArchiveRoot $ArchiveRoot `
                                  -CensorExePath $resolvedCensorExe `
                                  -ConfigPath $resolvedConfigPath `
                                  -DictionaryPath $resolvedDictionaryPath)

    if ($Repeat) {
        Write-Status "Waiting $RepeatIntervalMinutes minute(s) before next scan..."
        Start-Sleep -Seconds ([Math]::Max(1, $RepeatIntervalMinutes * 60))
    }
} while ($Repeat)