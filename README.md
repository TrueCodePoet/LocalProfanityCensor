# LocalProfanityCensor

LocalProfanityCensor is a local-first media profanity filtering tool for home media libraries. It detects configured words from subtitles or speech, then creates cleaned MKV outputs with muted, beeped, ducked, or prototype replacement audio, plus generated normal and censored subtitle tracks.

The project is designed for people who want private, offline processing and for developers who want a practical .NET, FFmpeg, and Whisper media pipeline they can inspect and extend.

> [!IMPORTANT]
> Goal: LocalProfanityCensor is a review-first assistive tool, not an authority. All generated output should be reviewed by a human before use, and users should not assume 100% detection, timing, subtitle, or replacement accuracy.

## What It Does

- Inspects media with `ffprobe` and renders/remuxes with `ffmpeg`.
- Uses embedded or sidecar subtitles when available.
- Uses local faster-whisper transcription when subtitles are missing, incomplete, or need timing refinement.
- Supports Demucs dialogue/vocal isolation for better speech recognition on difficult audio.
- Adds a selectable `Clean Censored` audio track while preserving the original audio track by default.
- Creates a generated normal subtitle track when the source media does not already have a usable normal subtitle.
- Creates a generated censored subtitle track with configured words replaced.
- Targets MKV output so video, original audio, clean audio, original subtitles, generated normal subtitles, and generated censored subtitles can live in one container.
- Writes JSON/CSV reports so users can review what was detected and changed.
- Includes a Windows batch runner with safe holding-folder mode, retries, state files, and multi-machine locks.

## Current Status

The `mute`, `beep`, and `duck` paths are the practical modes today. `replace` mode exists as a prototype for AI-generated word replacement and requires extra optional models and setup.

The default public profile is conservative about the original media: original audio remains default, original subtitles remain default when present, and clean/censored outputs are added as selectable tracks. If a video has no retained normal subtitle, the default profile can generate one from the selected transcript and embed it into the output MKV.

## Supported vs Experimental

### Supported for the first public release

- `inspect`
- `validate-dictionary`
- `transcribe`
- `health-check`
- `process-file`
- `process`
- Practical censor modes: `mute`, `beep`, and `duck`
- Subtitle recovery workflows such as generating a normal subtitle only when one is missing

These are the workflows the project is currently positioned to support for normal public use, subject to the environment and model prerequisites documented below.

### Experimental or prototype

- `replace` mode
- `prepare-replace-prototype`
- `synthesize-replace-prototype`
- alignment and replacement prototype workflows used for AI-generated replacement audio

Experimental features are included for testing and iteration. They should not be treated as production-ready, and all output from these paths should be reviewed carefully.

## Platform Support

LocalProfanityCensor is currently a **Windows-first** project.

- The included scripts target PowerShell on Windows.
- CI currently runs on `windows-latest`.
- Packaging and installer assets are Windows-oriented.

The core .NET application may be portable in parts, but this repository should currently be treated as **tested and documented for Windows workflows first**.

## Why MKV

LocalProfanityCensor targets MKV for video outputs because MKV is a practical archival container for this workflow. A single MKV can hold the copied video stream, original audio tracks, the added `Clean Censored` audio track, retained original subtitles, generated normal subtitles, generated censored subtitles, track titles, languages, and default-track flags.

The tool can read common video inputs such as `.mkv`, `.mp4`, `.m4v`, `.avi`, and `.m2ts`, but video outputs are normalized to `.mkv` so everything stays embedded together instead of being split across sidecar files.

## Repository Layout

```text
src/LocalProfanityCensor.DotNet/   .NET 10 command-line application
Deployment/                        Windows publish, staging, MSI, and batch scripts
bundles/                           Opinionated public bundle targets for supported workflows
examples/                          Small generic scripts for common workflows
models/                            Placeholder and instructions for local model downloads
scripts/                           Utility scripts, including model download helper
docs/                              Setup guidance for users and AI coding agents
THIRD_PARTY_NOTICES.md             Dependency and model attribution notes
```

## Recommended Public Bundle

For the first public release, the recommended starting point is the Windows mute bundle:

- `bundles\windows-mute-default`

That bundle documents one supported workflow, provides bundle-local build/setup/run helpers, and keeps generated payload output out of source control. It is the best path for users who want the practical Windows workflow without choosing between every internal config and script.

Typical bundle flow:

```powershell
pwsh .\bundles\windows-mute-default\Build-Bundle.ps1
pwsh .\bundles\windows-mute-default\Setup-This-Bundle.ps1 -Profile gpu

pwsh .\bundles\windows-mute-default\Run-This-Bundle.ps1 `
	-InputRoot "D:\Media\Incoming" `
	-HoldingRoot "D:\Media\CensoredHolding" `
	-MaxFilesPerRun 1
```

Use `-Profile cpu` on machines without a supported NVIDIA CUDA setup.

## Prerequisites

- Windows PowerShell 7+ for the included scripts.
- .NET 10 SDK for building from source.
- `ffmpeg` and `ffprobe` available on `PATH`.
- Python 3.10 or newer for faster-whisper and Demucs.
- Optional NVIDIA CUDA stack if you want GPU acceleration.

## Minimum Working Setup

If you want the simplest supported starting point, use this baseline:

- Windows with PowerShell 7+
- .NET 10 SDK
- `ffmpeg` and `ffprobe` on `PATH`
- Python 3.10+
- `faster-whisper`, `demucs`, and `pyyaml` installed through `Deployment\Scripts\Setup-AI.ps1`
- a downloaded Whisper model such as `large-v3` or `medium`
- the example dictionary at `src\LocalProfanityCensor.DotNet\Dictionaries\profanity.example.yml`

Recommended first-run workflow:

1. Build the project.
2. Run `Setup-AI.ps1` with `-SkipOpenVoice`.
3. Download a Whisper model.
4. Validate the dictionary.
5. Run a `-DryRun` or use `mute` mode on a single test file.

For the lowest-risk first success, start with one of these:

- `pwsh .\examples\Process-OneFile.ps1 -InputPath "D:\Media\ExampleMovie.mkv" -DryRun`
- `pwsh .\examples\Generate-SubtitleIfMissing.ps1 -InputPath "D:\Media\OlderMovie.mkv"`

## Quick Start

Clone the repo and build the app:

```powershell
dotnet build .\src\LocalProfanityCensor.DotNet\LocalProfanityCensor.DotNet.csproj
```

Create the AI runtime:

```powershell
pwsh .\Deployment\Scripts\Setup-AI.ps1 -Profile gpu -SkipOpenVoice
```

If you do not have a compatible GPU, use `-Profile cpu` instead. Leave `-SkipOpenVoice` on unless you are intentionally testing prototype replacement audio.

Download the default faster-whisper model:

```powershell
pwsh .\scripts\Download-FasterWhisperModel.ps1 -ModelName large-v3
```

Validate the starter dictionary:

```powershell
dotnet run --project .\src\LocalProfanityCensor.DotNet -- validate-dictionary .\src\LocalProfanityCensor.DotNet\Dictionaries\profanity.example.yml
```

Process one file:

```powershell
pwsh .\examples\Process-OneFile.ps1 `
	-InputPath "D:\Media\ExampleMovie.mkv" `
	-OutputPath "D:\Media\ExampleMovie.clean.mkv" `
	-Mode mute
```

Start with `-DryRun` if you want reports without writing a cleaned media file.

Generate a normal subtitle only when the source media is missing one:

```powershell
pwsh .\examples\Generate-SubtitleIfMissing.ps1 `
	-InputPath "D:\Media\OlderMovie.mkv"
```

This is a conservative workflow for older media libraries where the main goal is subtitle recovery rather than censorship. It generates a normal subtitle track only if a usable one is missing.

## Folder Batch Example

For a home media folder, use holding mode first. This keeps originals in place and writes cleaned files to a separate folder.

```powershell
pwsh .\examples\Batch-ProcessFolder.ps1 `
	-InputRoot "D:\Media\Incoming" `
	-HoldingRoot "D:\Media\CensoredHolding" `
	-MaxFilesPerRun 1
```

The batch runner publishes and stages the app, copies each claimed file into local work, validates the output, and records state so failed or completed jobs are handled predictably. Use `-ReplaceOriginal` only after you have reviewed outputs and provided an `-ArchiveRoot`.

## Configuration

Profiles live under `src\LocalProfanityCensor.DotNet\Configs`.

- `production.demucs.depth.censored-option.yml` is the recommended default. It uses faster-whisper `large-v3`, Demucs depth isolation, mute mode, generated normal subtitles when missing, generated censored subtitles, and original tracks as defaults.
- `production.demucs.depth.medium.yml` uses the same deeper Demucs path with the smaller `medium` model.
- `production.demucs.speed.medium.yml` favors lower runtime cost.

The key subtitle settings are:

- `subtitles.generate_plain_subtitle_if_missing`: create a normal generated subtitle when the media has no retained normal subtitle.
- `subtitles.generate_censored_subtitle`: create a censored subtitle track with configured words replaced.
- `subtitles.default_censored_subtitle`: when `false`, original or generated normal subtitles remain default and censored subtitles stay selectable.

For a subtitle-recovery workflow, see `src\LocalProfanityCensor.DotNet\Configs\subtitle-only.if-missing.medium.yml` and `examples\Generate-SubtitleIfMissing.ps1`.

The starter dictionary lives at `src\LocalProfanityCensor.DotNet\Dictionaries\profanity.example.yml`. Treat it as an example, not a universal policy. Edit the terms, variants, severity, and replacement words to match your household or project requirements.

> [!WARNING]
> The example dictionary contains explicit and offensive terms because the tool needs real match configuration to validate detection, replacement, subtitles, and reports. You do not need to open that file unless you are reviewing or customizing the censor policy.

## Model Setup With An AI Agent

Model setup has enough moving pieces that an AI coding agent can be useful. See `docs\AI_AGENT_SETUP.md` for a copy/paste prompt and the exact checks an agent should perform. The guide tells agents to verify `.NET`, `ffmpeg`, `ffprobe`, Python packages, model cache paths, and dictionary validation without adding private paths or generated files to the repo.

## Troubleshooting

### `ffmpeg` or `ffprobe` not found

- Confirm both tools are installed and available on `PATH`.
- Open a new shell after updating `PATH`.
- Run `dotnet run --project .\src\LocalProfanityCensor.DotNet -- health-check --mode mute` to confirm the runtime can see them.

### Python runtime or package errors

- Re-run `pwsh .\Deployment\Scripts\Setup-AI.ps1 -Profile cpu -SkipOpenVoice` or the GPU variant you intend to use.
- Confirm `CENSOR_MEDIA_PYTHON` points to the Python environment where `faster-whisper`, `demucs`, and `pyyaml` are installed.
- If needed, activate the environment and verify imports manually before retrying the app.

### Missing model or model mismatch

- Download the model named in your selected config, such as `large-v3` or `medium`.
- Make sure the config's `transcription.model` matches the downloaded model.
- Confirm the model cache path is consistent with `CENSOR_MEDIA_HF_HOME`.

### Replace mode is not ready

- This is expected unless you intentionally set up the optional prototype dependencies.
- `replace` mode requires extra Python packages and model/checkpoint setup beyond the default supported workflow.
- Start with `mute`, `beep`, `duck`, or subtitle-only recovery first.

### Output is incomplete or needs review

- Review the JSON/CSV reports and generated subtitle tracks.
- Try a conservative single-file run before batch processing.
- Use `-DryRun` first when testing a new config or media type.

## Models And Licenses

Model weights are not included in this repository. Users download them locally and are responsible for following the upstream model cards and licenses. See `models\README.md` and `THIRD_PARTY_NOTICES.md` before redistributing binaries, installers, or model bundles.

## Privacy

LocalProfanityCensor is intended to run locally. Media files, transcripts, and reports stay on your machine unless you choose to move or publish them. The application disables implicit Hugging Face token use in its bridge environment by default.

Do not commit media files, generated reports, work folders, model weights, API keys, tokens, or personal library paths. The included `.gitignore` is set up to exclude those by default.

## Developer Commands

```powershell
dotnet build .\src\LocalProfanityCensor.DotNet\LocalProfanityCensor.DotNet.csproj
dotnet run --project .\src\LocalProfanityCensor.DotNet -- inspect "D:\Media\ExampleMovie.mkv"
dotnet run --project .\src\LocalProfanityCensor.DotNet -- validate-dictionary .\src\LocalProfanityCensor.DotNet\Dictionaries\profanity.example.yml
```

Publish and stage a Windows payload:

```powershell
pwsh .\Deployment\Scripts\Publish-Core.ps1
pwsh .\Deployment\Scripts\Stage-Payload.ps1
```

Build the optional MSI after staging:

```powershell
pwsh .\Deployment\Scripts\Build-Msi.ps1
```

## Important Limits

- Always review generated output before replacing originals.
- This is a tooling aid, not a guarantee of correctness; do not assume 100% accuracy.
- Speech recognition quality depends on the audio, model, hardware, and config.
- Generated subtitles depend on transcript quality; ASR refinement helps, but subtitle wording and timing should still be reviewed.
- Replacement audio is experimental and requires separate model checkpoints.
- This tool does not grant rights to modify or redistribute media you do not own or have permission to process.

## License

LocalProfanityCensor is released under the MIT License. Third-party tools, packages, and models keep their own licenses and terms.
