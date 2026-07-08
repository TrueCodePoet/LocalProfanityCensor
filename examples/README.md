# Examples

These scripts are intentionally generic. They do not assume a specific media library layout, drive letter, or personal folder structure.

## Process one file

```powershell
pwsh .\examples\Process-OneFile.ps1 `
  -InputPath "D:\Media\ExampleMovie.mkv" `
  -OutputPath "D:\Media\ExampleMovie.clean.mkv" `
  -Mode mute
```

Use `-DryRun` to produce reports without writing a cleaned media file.

Video outputs are written as MKV so the result can contain the original video/audio, the added clean censored audio track, generated normal subtitles when needed, and generated censored subtitles in the same file.

## Generate a normal subtitle only if missing

```powershell
pwsh .\examples\Generate-SubtitleIfMissing.ps1 `
  -InputPath "D:\Media\OlderMovie.mkv"
```

This example is useful for older media that has no usable subtitle track. It keeps the workflow conservative by generating a normal subtitle only when one is missing and does not add a censored subtitle track.

The default config for this example is `src\LocalProfanityCensor.DotNet\Configs\subtitle-only.if-missing.medium.yml`.

## Process a folder safely

```powershell
pwsh .\examples\Batch-ProcessFolder.ps1 `
  -InputRoot "D:\Media\Incoming" `
  -HoldingRoot "D:\Media\CensoredHolding" `
  -MaxFilesPerRun 1
```

The batch example publishes the app, stages the payload, copies each claimed file into local work, writes completed MKV files to the holding folder, and keeps originals in place unless `-ReplaceOriginal` is explicitly provided. If the source media has no usable normal subtitle, the default config can generate one and embed it alongside the censored subtitle option.