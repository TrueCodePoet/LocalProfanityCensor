# Deployment

The deployment folder contains Windows-oriented packaging and batch-processing helpers. The scripts use repo-relative defaults and can be run from any clone path.

## Scripts

- `Scripts\Publish-Core.ps1` publishes the .NET app as a self-contained Windows x64 single-file executable.
- `Scripts\Stage-Payload.ps1` stages the published executable, configs, and starter dictionary under `Deployment\artifacts\staging\LocalProfanityCensor`.
- `Scripts\Invoke-CensorMediaBatch.ps1` processes a media folder safely with shared state, lock files, retries, local work folders, and a holding output folder.
- `Scripts\Setup-AI.ps1` creates a Python virtual environment with `faster-whisper`, `demucs`, and optional replacement-mode packages.
- `Scripts\Build-Msi.ps1` builds the WiX installer from the staged payload.

## Safe Batch Mode

Start with holding mode. It leaves source files untouched and writes cleaned MKV files to a separate folder. MKV is the target output container because it can embed the original media streams, the clean censored audio track, and generated subtitle tracks together.

```powershell
pwsh .\Deployment\Scripts\Publish-Core.ps1
pwsh .\Deployment\Scripts\Stage-Payload.ps1

pwsh .\Deployment\Scripts\Invoke-CensorMediaBatch.ps1 `
  -InputRoot "D:\Media\Incoming" `
  -HoldingRoot "D:\Media\CensoredHolding" `
  -MaxFilesPerRun 1
```

Only use `-ReplaceOriginal` after you have validated output quality on your own media. Replacement mode archives the original to `-ArchiveRoot` before moving the cleaned MKV into the source folder.

## AI Runtime

The batch runner uses `CENSOR_MEDIA_PYTHON` or `-MediaPythonPath` to locate a Python environment with the AI packages installed. It uses `CENSOR_MEDIA_HF_HOME` or `-HuggingFaceCacheRoot` for the Hugging Face model cache.

Model weights are not bundled in the installer. See `models\README.md` and `scripts\Download-FasterWhisperModel.ps1`.

For the normal mute/beep/duck path, run setup with `-SkipOpenVoice`. Only omit that switch when you are intentionally testing prototype replacement audio.

## State And Locks

By default, state is stored under `<HoldingRoot>\.state`. Each source-relative media file gets its own JSON state file and lock file so multiple workers can safely share the same input and holding folders.

The runner skips completed jobs when the source file size and timestamp still match. Failed jobs retry according to `-MaxAttempts` and `-RetryDelayMinutes`.

## Output Behavior

Default behavior is safe for testing:

- Originals stay in place.
- Cleaned output goes to the holding folder.
- Original audio remains the default audio track.
- Existing original subtitles remain default.
- If no retained normal subtitle exists, a generated normal subtitle can be embedded into the output MKV.
- A generated censored subtitle can be embedded as a selectable clean subtitle option.
- Censored audio and generated subtitles are added as selectable options.
- Files with no detected profanity and no subtitle changes can be marked complete without writing a new MKV.

## Useful Parameters

- `-InputRoot`: source media folder to scan recursively.
- `-HoldingRoot`: holding folder for completed MKV outputs in safe mode.
- `-StateRoot`: shared JSON state and lock folder. Defaults to `<HoldingRoot>\.state`.
- `-ArchiveRoot`: original-file archive folder used by `-ReplaceOriginal`.
- `-LocalWorkRoot`: machine-local working directory. Defaults to `%TEMP%\LocalProfanityCensorBatch`.
- `-MaxFilesPerRun`: limit for one pass. `0` means no explicit limit.
- `-Repeat`: keep scanning after each pass.
- `-DryRun`: pass `--dry-run` to the censor process and do not move a final output.
- `-KeepWork`: preserve local work folders and pass `--keep-work` to the censor process.
- `-RunSetupAI`: run `Scripts\Setup-AI.ps1` before scanning.
- `-MediaPythonPath`: explicitly set the Python runtime used by ASR/Demucs.
- `-HuggingFaceCacheRoot`: explicitly set the Hugging Face cache root used by ASR models.