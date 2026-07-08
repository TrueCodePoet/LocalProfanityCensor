# Contributing

Contributions are welcome. Keep changes focused, testable, and friendly to home users who may not be developers.

## Guidelines

- Do not commit media files, transcripts, generated reports, model weights, local cache folders, or personal paths.
- Keep examples generic and path-agnostic.
- Prefer clear configuration options over hard-coded local assumptions.
- Update documentation when a user-facing command, config key, or workflow changes.
- Preserve original media by default; destructive or replacement workflows must be explicit.

## Validation

Before opening a pull request, run:

```powershell
dotnet build .\src\LocalProfanityCensor.DotNet\LocalProfanityCensor.DotNet.csproj
dotnet run --project .\src\LocalProfanityCensor.DotNet -- validate-dictionary .\src\LocalProfanityCensor.DotNet\Dictionaries\profanity.example.yml
```

If your change touches deployment scripts, also run:

```powershell
pwsh .\Deployment\Scripts\Publish-Core.ps1
pwsh .\Deployment\Scripts\Stage-Payload.ps1
```