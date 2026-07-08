using System.Text.Json;
using LocalProfanityCensor.DotNet.Models;

namespace LocalProfanityCensor.DotNet.Services;

internal static class CosyVoicePrototypeService
{
    private static readonly string? CosyVoiceRepoPath = Environment.GetEnvironmentVariable("CENSOR_COSYVOICE_REPO");
    private static readonly string? CosyVoiceMatchaPath = string.IsNullOrWhiteSpace(CosyVoiceRepoPath)
        ? null
        : Path.Combine(CosyVoiceRepoPath, "third_party", "Matcha-TTS");

    public static async Task<ReplaceSynthesisResult> RunAsync(
        string manifestPath,
        string outputDir,
        string requestedDevice)
    {
        var manifest = await ReplaceManifestLoader.LoadAsync(manifestPath);
        var manifestFileName = Path.GetFileName(manifestPath);
        Directory.CreateDirectory(outputDir);
        var backgroundClipPath = ReplaceManifestLoader.ResolveBackgroundClipPath(manifest);

        var result = new ReplaceSynthesisResult
        {
            Engine = "cosyvoice",
            ManifestPath = manifestPath,
            ReplacementText = string.IsNullOrWhiteSpace(manifest.ReplacementPhraseText) ? manifest.ReplacementText : manifest.ReplacementPhraseText,
            ReferenceClip = manifest.ReferenceClip.Path,
            TargetClip = manifest.TargetClip.Path,
            BackgroundClip = backgroundClipPath,
        };

        var pythonExecutable = ResolveCosyVoicePythonExecutable();
        if (pythonExecutable is null)
        {
            result.Status = "unavailable";
            result.Message = "No Python executable with CosyVoice was found. Set CENSOR_COSYVOICE_PYTHON or install the dependencies into the configured runtime.";
            return result;
        }

        var modelDir = ResolveCosyVoiceModelDirectory();
        if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir))
        {
            result.Status = "unavailable";
            result.Message = "CosyVoice model directory was not found. Set CENSOR_COSYVOICE_MODEL_DIR.";
            return result;
        }

        var bridgeScriptPath = Path.Combine(AppContext.BaseDirectory, "Assets", "cosyvoice_replace_bridge.py");
        if (!File.Exists(bridgeScriptPath))
        {
            result.Status = "failed";
            result.Message = $"CosyVoice bridge script was not found: {bridgeScriptPath}";
            return result;
        }

        var rawGeneratedPath = Path.Combine(outputDir, "replacement.cosyvoice.raw.wav");
        var trimmedGeneratedPath = Path.Combine(outputDir, "replacement.cosyvoice.trimmed.wav");
        var fittedGeneratedPath = Path.Combine(outputDir, "replacement.cosyvoice.fitted.wav");
        var replacedVocalPath = Path.Combine(outputDir, "target.vocals.replaced.wav");
        var previewMixPath = Path.Combine(outputDir, "target.preview.mix.wav");

        try
        {
            var environmentVariables = new Dictionary<string, string?>(AsrBridgeService.BuildBridgeEnvironment(), StringComparer.OrdinalIgnoreCase);
            ApplyCosyVoicePythonPath(environmentVariables);

            var commandResult = await ToolRunner.RunCaptureAsync(
                pythonExecutable,
                environmentVariables,
                TimeSpan.FromHours(1),
                new ToolRunner.CommandProgressInfo("replace", "Running CosyVoice synthesis", null, manifestFileName, "replace"),
                bridgeScriptPath,
                manifestPath,
                rawGeneratedPath,
                ResolveDevice(requestedDevice),
                modelDir);

            result.Warnings.AddRange(commandResult.StandardError
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !string.IsNullOrWhiteSpace(line)));

            result.GeneratedClip = rawGeneratedPath;

            await ReplaceAudioPostProcessor.TrimGeneratedClipAsync(rawGeneratedPath, trimmedGeneratedPath, "CosyVoice");
            result.TrimmedGeneratedClip = File.Exists(trimmedGeneratedPath) ? trimmedGeneratedPath : rawGeneratedPath;

            var generatedDuration = await ReplaceAudioPostProcessor.ProbeAudioDurationAsync(result.TrimmedGeneratedClip);
            var targetWordStart = manifest.TargetClip.MatchStart ?? Math.Max(0.0, manifest.SelectedMatch.Start - manifest.TargetClip.Start);
            var targetWordEnd = manifest.TargetClip.MatchEnd ?? Math.Max(targetWordStart + 0.05, manifest.SelectedMatch.End - manifest.TargetClip.Start);
            var targetWindowDuration = Math.Max(0.05, manifest.TargetClip.End - manifest.TargetClip.Start);
            var relativeWordStart = Math.Clamp(targetWordStart, 0.0, targetWindowDuration);
            var availableReplacementDuration = Math.Max(0.05, targetWindowDuration - relativeWordStart);
            var targetReplacementDuration = Math.Min(availableReplacementDuration, generatedDuration);
            var relativeWordEnd = Math.Min(targetWindowDuration, relativeWordStart + targetReplacementDuration);

            if (Math.Abs(generatedDuration - targetReplacementDuration) <= 0.02)
            {
                File.Copy(result.TrimmedGeneratedClip, fittedGeneratedPath, overwrite: true);
            }
            else
            {
                await ReplaceAudioPostProcessor.FitGeneratedClipAsync(result.TrimmedGeneratedClip, fittedGeneratedPath, targetReplacementDuration, generatedDuration, "CosyVoice");
            }

            result.FittedGeneratedClip = fittedGeneratedPath;

            await ReplaceAudioPostProcessor.SpliceIntoTargetClipAsync(
                manifest.TargetClip.Path,
                fittedGeneratedPath,
                replacedVocalPath,
                relativeWordStart,
                relativeWordEnd,
                targetWindowDuration,
                "CosyVoice");
            result.ReplacedVocalClip = replacedVocalPath;

            await MediaRenderService.MixAudioTracksAsync(replacedVocalPath, backgroundClipPath, previewMixPath);
            result.PreviewMixClip = previewMixPath;
            result.Status = "completed";
            result.Message = "CosyVoice prototype synthesis and splice preview completed.";
            return result;
        }
        catch (Exception ex)
        {
            result.Status = "failed";
            result.Message = ex.Message;
            return result;
        }
    }

    private static string ResolveDevice(string requestedDevice)
    {
        if (string.IsNullOrWhiteSpace(requestedDevice))
        {
            return "auto";
        }

        return requestedDevice.Trim().ToLowerInvariant();
    }

    private static string? ResolveCosyVoiceModelDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("CENSOR_COSYVOICE_MODEL_DIR");
        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    private static string? ResolveCosyVoicePythonExecutable()
    {
        foreach (var candidate in EnumeratePythonCandidates())
        {
            if (CanImportCosyVoice(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumeratePythonCandidates()
    {
        foreach (var candidate in new[]
        {
            Environment.GetEnvironmentVariable("CENSOR_COSYVOICE_PYTHON"),
            Environment.GetEnvironmentVariable("CENSOR_MEDIA_PYTHON"),
            Environment.GetEnvironmentVariable("PYTHON"),
        })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static bool CanImportCosyVoice(string candidate)
    {
        try
        {
            var environment = new Dictionary<string, string?>(AsrBridgeService.BuildBridgeEnvironment(), StringComparer.OrdinalIgnoreCase);
            ApplyCosyVoicePythonPath(environment);

            ToolRunner.RunCaptureAsync(
                candidate,
                environment,
                TimeSpan.FromSeconds(30),
                "-c",
                "from cosyvoice.cli.cosyvoice import CosyVoice, CosyVoice2; print('ok')").GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyCosyVoicePythonPath(IDictionary<string, string?> environment)
    {
        var pythonPathEntries = new List<string>();

        if (Directory.Exists(CosyVoiceRepoPath))
        {
            pythonPathEntries.Add(CosyVoiceRepoPath);
        }

        if (Directory.Exists(CosyVoiceMatchaPath))
        {
            pythonPathEntries.Add(CosyVoiceMatchaPath);
        }

        environment.TryGetValue("PYTHONPATH", out var existingPythonPath);
        if (!string.IsNullOrWhiteSpace(existingPythonPath))
        {
            pythonPathEntries.Add(existingPythonPath);
        }

        if (pythonPathEntries.Count > 0)
        {
            environment["PYTHONPATH"] = string.Join(Path.PathSeparator, pythonPathEntries);
        }
    }
}