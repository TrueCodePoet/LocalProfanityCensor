# AI Agent Setup Guide

This file is written for users who want GitHub Copilot, another coding agent, or a local assistant to help set up LocalProfanityCensor. The goal is to let the agent verify prerequisites and configure paths without exposing private media folders or secrets.

## What The Agent Should Do

Ask the agent to work inside the cloned repository and perform these checks:

1. Verify `.NET 10 SDK` is installed with `dotnet --info`.
2. Verify `ffmpeg` and `ffprobe` are available on `PATH`.
3. Create or identify a Python environment for media AI packages.
4. Install `faster-whisper`, `demucs`, and `pyyaml` into that Python environment.
5. Set `CENSOR_MEDIA_PYTHON` to that Python executable.
6. Choose a local Hugging Face cache folder and set `CENSOR_MEDIA_HF_HOME`.
7. Download the configured faster-whisper model, usually `large-v3`.
8. Run `dotnet build` and dictionary validation.
9. Run a `-DryRun` on one test media file chosen by the user.

The agent should not upload media, transcripts, reports, model files, tokens, local library paths, or generated work folders.

> [!WARNING]
> The starter dictionary contains explicit and offensive terms. Agents should validate the file path as configuration, but should not print, summarize, or expand the dictionary contents unless the user explicitly asks to customize it.

## Suggested Prompt

```text
Set up this LocalProfanityCensor repository on my machine for local/offline media processing. Verify .NET 10, ffmpeg, ffprobe, and Python. Create a Python virtual environment if needed, install faster-whisper, demucs, and pyyaml, set CENSOR_MEDIA_PYTHON and CENSOR_MEDIA_HF_HOME for this shell, download the large-v3 faster-whisper model using scripts/Download-FasterWhisperModel.ps1, build the .NET project, validate the starter dictionary, and show me the final commands I should use for a dry run. Do not commit generated files, model weights, media files, transcripts, reports, or machine-specific paths.
```

## Commands The Agent Can Use

```powershell
dotnet --info
ffmpeg -version
ffprobe -version

python -m venv .venv
.\.venv\Scripts\python.exe -m pip install --upgrade pip
.\.venv\Scripts\python.exe -m pip install faster-whisper demucs pyyaml

$env:CENSOR_MEDIA_PYTHON = (Resolve-Path .\.venv\Scripts\python.exe).Path
$env:CENSOR_MEDIA_HF_HOME = (Resolve-Path .\models).Path

pwsh .\scripts\Download-FasterWhisperModel.ps1 -ModelName large-v3

dotnet build .\src\LocalProfanityCensor.DotNet\LocalProfanityCensor.DotNet.csproj
dotnet run --project .\src\LocalProfanityCensor.DotNet -- validate-dictionary .\src\LocalProfanityCensor.DotNet\Dictionaries\profanity.example.yml
```

## Model Verification

The download helper calls `faster_whisper.utils.download_model` and prints the resolved local model path. If users prefer another model size, update `transcription.model` in the selected YAML config and run the helper with the same model name.

Common model choices:

- `medium`: smaller and faster, lower recall.
- `large-v3`: better recall and the recommended default, but slower and larger.

If a dry run fails during model preflight, check that:

- `CENSOR_MEDIA_PYTHON` points to the Python environment where `faster-whisper` is installed.
- `CENSOR_MEDIA_HF_HOME` points to the same cache used by the download helper.
- The selected config's `transcription.model` matches the downloaded model.

## Privacy Rules For Agents

Agents should use placeholder paths in examples, such as `D:\Media\Incoming`, unless the user explicitly provides a path for a local dry run. Agents should not add real media names, private server names, API keys, tokens, or personal folder layouts to README files, examples, issues, commits, or documentation.