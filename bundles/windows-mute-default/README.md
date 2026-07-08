# Windows Mute Default Bundle

This bundle defines the default recommended packaging target for LocalProfanityCensor.

It is intentionally narrow:

- Windows-first
- `mute` mode first
- one recommended production config
- one review-first batch workflow
- no prototype replacement requirements

## Purpose

The purpose of this bundle is to reduce operational complexity for the most practical public workflow. Instead of asking users to understand the full repository structure, this bundle provides one targeted run shape with one supported path.

## Bundle Layout

This folder now acts as the structured target for the recommended bundle.

```text
bundles/windows-mute-default/
  README.md
  Build-Bundle.ps1
  Setup-This-Bundle.ps1
  Run-This-Bundle.ps1
  Payload/
  Configs/
  Dictionaries/
  Scripts/
```

## Intended User

This bundle is for users who want to:

- process local media on Windows
- keep original files untouched during review
- generate cleaned MKV outputs in a holding folder
- use the stable mute workflow instead of prototype replacement audio

## Recommended Config

This bundle is centered on:

- `Configs/production.demucs.depth.censored-option.yml`

That config currently represents the conservative production-oriented path:

- faster-whisper `large-v3`
- Demucs dialog isolation
- `mute` censor mode
- generated normal subtitle when missing
- generated censored subtitle as a selectable option
- original tracks preserved by default

## What This Bundle Contains

This bundle is designed to hold everything needed for the targeted run except user media, model weights, and machine-specific Python runtime contents.

Included in this folder structure:

- bundle-specific README and helper scripts
- `Payload/` for staged application output
- `Configs/` for the recommended runtime config set
- `Dictionaries/` for the starter dictionary
- `Scripts/` for copied bundle-local helper scripts

## External Prerequisites

These are still expected outside the repository bundle contents:

- `ffmpeg`
- `ffprobe`
- Python runtime for AI packages
- downloaded faster-whisper model weights

## How To Build This Bundle

Run the bundle build helper from the repository root or from this bundle folder:

```powershell
pwsh .\bundles\windows-mute-default\Build-Bundle.ps1
```

The build helper uses the existing deployment flow to:

1. publish the application
2. stage the payload
3. copy the staged payload into `Payload/`
4. copy the recommended configs into `Configs/`
5. copy the starter dictionary into `Dictionaries/`
6. copy the targeted helper scripts into `Scripts/`

## How To Set Up This Bundle

Use the bundle-local setup helper:

```powershell
pwsh .\bundles\windows-mute-default\Setup-This-Bundle.ps1 -Profile gpu
```

This delegates to the existing AI setup script with `-SkipOpenVoice` so the bundle stays focused on the stable mute workflow.

## How To Run This Bundle

Use the bundle-local run helper:

```powershell
pwsh .\bundles\windows-mute-default\Run-This-Bundle.ps1 `
  -InputRoot "D:\Media\Incoming" `
  -HoldingRoot "D:\Media\CensoredHolding" `
  -MaxFilesPerRun 1
```

The run helper uses the bundle-local payload, config, and dictionary by default.

## Validation Expectations

A built bundle for this target should be considered healthy only after these checks succeed:

- bundle build succeeds
- dictionary validation succeeds
- `health-check --mode mute` succeeds
- one single-file or one-file batch test succeeds on local media

## Explicit Non-Goals

This bundle does not define the public default for:

- `replace` mode
- OpenVoice or MeloTTS setup
- experimental synthesis workflows
- non-Windows packaging
- broad multi-profile support in one package

Those can be documented separately, but they should not dilute this bundle's purpose.

## Why This Bundle Exists

The repository supports more than one workflow, but the public release should lead with one stable path. This bundle exists to make that path obvious, documentable, and repeatable.
