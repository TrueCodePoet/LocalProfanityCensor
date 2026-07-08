using System.Text.Json;
using System.Text.Json.Serialization;
using LocalProfanityCensor.DotNet.Models;

namespace LocalProfanityCensor.DotNet.Services;

internal static class OpenVoicePrototypeService
{
    public static async Task<ReplaceSynthesisResult> RunAsync(
        string manifestPath,
        string outputDir,
        string requestedDevice,
        string? checkpointsDir,
        string speakerId,
        string language)
    {
        var manifest = await LoadManifestAsync(manifestPath);
        var manifestFileName = Path.GetFileName(manifestPath);
        Directory.CreateDirectory(outputDir);
        var backgroundClipPath = ResolveBackgroundClipPath(manifest);

        var result = new ReplaceSynthesisResult
        {
            Engine = "openvoice",
            ManifestPath = manifestPath,
            ReplacementText = manifest.ReplacementText,
            ReferenceClip = manifest.ReferenceClip.Path,
            TargetClip = manifest.TargetClip.Path,
            BackgroundClip = backgroundClipPath,
        };

        var pythonExecutable = ResolveOpenVoicePythonExecutable();
        if (pythonExecutable is null)
        {
            result.Status = "unavailable";
            result.Message = "No Python executable with both OpenVoice and MeloTTS was found. Set CENSOR_OPENVOICE_PYTHON or install the dependencies into the configured runtime.";
            return result;
        }

        var resolvedCheckpointsDir = ResolveCheckpointsDirectory(checkpointsDir);
        if (string.IsNullOrWhiteSpace(resolvedCheckpointsDir) || !Directory.Exists(resolvedCheckpointsDir))
        {
            result.Status = "unavailable";
            result.Message = "OpenVoice checkpoints directory was not found. Set CENSOR_OPENVOICE_CHECKPOINTS or pass --checkpoints-dir.";
            return result;
        }

        var bridgeScriptPath = Path.Combine(AppContext.BaseDirectory, "Assets", "openvoice_replace_bridge.py");
        if (!File.Exists(bridgeScriptPath))
        {
            result.Status = "failed";
            result.Message = $"OpenVoice bridge script was not found: {bridgeScriptPath}";
            return result;
        }

        var rawGeneratedPath = Path.Combine(outputDir, "replacement.openvoice.raw.wav");
        var trimmedGeneratedPath = Path.Combine(outputDir, "replacement.openvoice.trimmed.wav");
        var fittedGeneratedPath = Path.Combine(outputDir, "replacement.openvoice.fitted.wav");
        var replacedVocalPath = Path.Combine(outputDir, "target.vocals.replaced.wav");
        var previewMixPath = Path.Combine(outputDir, "target.preview.mix.wav");

        try
        {
            var environmentVariables = new Dictionary<string, string?>(AsrBridgeService.BuildBridgeEnvironment(), StringComparer.OrdinalIgnoreCase);
            var huggingFaceCacheDir = Path.Combine(outputDir, ".hf-cache");
            Directory.CreateDirectory(huggingFaceCacheDir);
            environmentVariables["HF_HOME"] = huggingFaceCacheDir;
            environmentVariables["TRANSFORMERS_CACHE"] = huggingFaceCacheDir;
            var commandResult = await ToolRunner.RunCaptureAsync(
                pythonExecutable,
                environmentVariables,
                TimeSpan.FromHours(1),
                new ToolRunner.CommandProgressInfo("replace", "Running OpenVoice synthesis", null, manifestFileName, "replace"),
                bridgeScriptPath,
                manifestPath,
                rawGeneratedPath,
                ResolveOpenVoiceDevice(requestedDevice),
                resolvedCheckpointsDir,
                speakerId,
                language);

            result.Warnings.AddRange(commandResult.StandardError
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !string.IsNullOrWhiteSpace(line)));

            result.GeneratedClip = rawGeneratedPath;

            await TrimGeneratedClipAsync(rawGeneratedPath, trimmedGeneratedPath);
            result.TrimmedGeneratedClip = File.Exists(trimmedGeneratedPath) ? trimmedGeneratedPath : rawGeneratedPath;

            var generatedDuration = await ProbeAudioDurationAsync(result.TrimmedGeneratedClip);
            var targetWordDuration = Math.Max(0.05, manifest.SelectedMatch.End - manifest.SelectedMatch.Start);
            var targetWindowDuration = Math.Max(0.05, manifest.TargetClip.End - manifest.TargetClip.Start);
            var relativeWordStart = Math.Max(0.0, manifest.SelectedMatch.Start - manifest.TargetClip.Start);
            var availableReplacementDuration = Math.Max(0.05, targetWindowDuration - relativeWordStart);
            var useTrimmedClipDirectly = generatedDuration <= availableReplacementDuration;
            var targetReplacementDuration = useTrimmedClipDirectly
                ? generatedDuration
                : Math.Min(availableReplacementDuration, Math.Max(targetWordDuration, generatedDuration));
            var relativeWordEnd = Math.Min(targetWindowDuration, relativeWordStart + targetReplacementDuration);

            if (useTrimmedClipDirectly)
            {
                File.Copy(result.TrimmedGeneratedClip, fittedGeneratedPath, overwrite: true);
            }
            else
            {
                await FitGeneratedClipAsync(result.TrimmedGeneratedClip, fittedGeneratedPath, targetReplacementDuration, generatedDuration);
            }

            result.FittedGeneratedClip = fittedGeneratedPath;

            await SpliceIntoTargetClipAsync(
                manifest.TargetClip.Path,
                fittedGeneratedPath,
                replacedVocalPath,
                relativeWordStart,
                relativeWordEnd,
                targetWindowDuration);
            result.ReplacedVocalClip = replacedVocalPath;

            await MediaRenderService.MixAudioTracksAsync(replacedVocalPath, backgroundClipPath, previewMixPath);
            result.PreviewMixClip = previewMixPath;
            result.Status = "completed";
            result.Message = "OpenVoice prototype synthesis and splice preview completed.";
            return result;
        }
        catch (Exception ex)
        {
            result.Status = "failed";
            result.Message = ex.Message;
            return result;
        }
    }

    private static async Task TrimGeneratedClipAsync(string inputPath, string outputPath)
    {
        await ToolRunner.RunAsync(
            "ffmpeg",
            new ToolRunner.CommandProgressInfo("replace", "Trimming generated replacement clip", null, Path.GetFileName(inputPath), "replace"),
            "-y",
            "-i",
            inputPath,
            "-af",
            "silenceremove=start_periods=1:start_threshold=-45dB:stop_periods=-1:stop_threshold=-45dB",
            outputPath);
    }

    private static async Task FitGeneratedClipAsync(string inputPath, string outputPath, double targetDuration, double inputDuration)
    {
        var effectiveInputDuration = Math.Max(0.05, inputDuration);
        var tempoRatio = Math.Clamp(effectiveInputDuration / Math.Max(0.05, targetDuration), 0.25, 8.0);
        var tempoFilter = BuildAtempoFilter(tempoRatio);
        var trimEnd = targetDuration.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

        await ToolRunner.RunAsync(
            "ffmpeg",
            new ToolRunner.CommandProgressInfo("replace", "Fitting generated replacement clip", null, Path.GetFileName(inputPath), "replace"),
            "-y",
            "-i",
            inputPath,
            "-af",
            $"{tempoFilter},apad=pad_dur={trimEnd},atrim=0:{trimEnd}",
            outputPath);
    }

    private static async Task SpliceIntoTargetClipAsync(
        string targetClipPath,
        string replacementClipPath,
        string outputPath,
        double relativeWordStart,
        double relativeWordEnd,
        double targetWindowDuration)
    {
        var replacementDuration = await ProbeAudioDurationAsync(replacementClipPath);
        var crossfadeDuration = Math.Min(
            0.06,
            Math.Min(
                Math.Max(0.0, relativeWordStart),
                Math.Max(0.0, Math.Min((relativeWordEnd - relativeWordStart) / 2.0, replacementDuration / 2.0))));

        var fadeOutStart = Math.Max(0.0, relativeWordStart - crossfadeDuration);
        var fadeOutDuration = Math.Max(0.001, Math.Min(crossfadeDuration, relativeWordStart - fadeOutStart));
        var fadeInStart = Math.Min(targetWindowDuration, relativeWordEnd);
        var fadeInDuration = Math.Max(0.001, Math.Min(crossfadeDuration, targetWindowDuration - fadeInStart));
        var mutedStart = Math.Max(0.0, relativeWordStart - crossfadeDuration);
        var mutedEnd = Math.Min(targetWindowDuration, relativeWordEnd + crossfadeDuration);

        replacementDuration = Math.Max(0.001, replacementDuration);
        var replacementFadeDuration = Math.Max(0.001, Math.Min(crossfadeDuration, replacementDuration / 2.0));
        var replacementFadeOutStart = Math.Max(0.0, replacementDuration - replacementFadeDuration);

        var mutedStartSeconds = mutedStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        var mutedEndSeconds = mutedEnd.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        var replacementFadeDurationSeconds = replacementFadeDuration.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        var replacementFadeOutStartSeconds = replacementFadeOutStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        var targetDurationSeconds = targetWindowDuration.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        var delayMs = Math.Max(0, (int)Math.Round(relativeWordStart * 1000.0));
        var filter = string.Join(';', new[]
        {
            $"[0:a]volume=enable='between(t,{mutedStartSeconds},{mutedEndSeconds})':volume=0.15[a0]",
            $"[1:a]aresample=44100,aformat=channel_layouts=stereo,afade=t=in:st=0:d={replacementFadeDurationSeconds},afade=t=out:st={replacementFadeOutStartSeconds}:d={replacementFadeDurationSeconds},adelay={delayMs}|{delayMs},apad=pad_dur={targetDurationSeconds},atrim=0:{targetDurationSeconds}[a1]",
            "[a0][a1]amix=inputs=2[aout]",
        });

        await ToolRunner.RunAsync(
            "ffmpeg",
            new ToolRunner.CommandProgressInfo("replace", "Splicing replacement into target clip", null, Path.GetFileName(targetClipPath), "replace"),
            "-y",
            "-i",
            targetClipPath,
            "-i",
            replacementClipPath,
            "-filter_complex",
            filter,
            "-map",
            "[aout]",
            outputPath);
    }

    private static string BuildAtempoFilter(double ratio)
    {
        var factors = new List<double>();
        var remaining = ratio;
        while (remaining > 2.0)
        {
            factors.Add(2.0);
            remaining /= 2.0;
        }

        while (remaining < 0.5)
        {
            factors.Add(0.5);
            remaining /= 0.5;
        }

        factors.Add(remaining);
        return string.Join(',', factors.Select(value => $"atempo={value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}"));
    }

    private static async Task<double> ProbeAudioDurationAsync(string path)
    {
        var mediaInfo = await MediaInspector.InspectAsync(path);
        return mediaInfo.DurationSeconds;
    }

    private static string ResolveOpenVoiceDevice(string requestedDevice)
    {
        if (string.IsNullOrWhiteSpace(requestedDevice))
        {
            return "auto";
        }

        return requestedDevice.Trim().ToLowerInvariant();
    }

    private static string ResolveBackgroundClipPath(ReplacePrototypeManifest manifest)
    {
        if (!string.IsNullOrWhiteSpace(manifest.BackgroundClip.Path))
        {
            return manifest.BackgroundClip.Path;
        }

        var targetClipDirectory = Path.GetDirectoryName(manifest.TargetClip.Path);
        if (!string.IsNullOrWhiteSpace(targetClipDirectory))
        {
            var siblingBackgroundClip = Path.Combine(targetClipDirectory, "target.background.wav");
            if (File.Exists(siblingBackgroundClip))
            {
                return siblingBackgroundClip;
            }
        }

        return manifest.BackgroundStemPath;
    }

    private static string? ResolveCheckpointsDirectory(string? requestedCheckpointsDir)
    {
        if (!string.IsNullOrWhiteSpace(requestedCheckpointsDir))
        {
            return Path.GetFullPath(requestedCheckpointsDir);
        }

        var configured = Environment.GetEnvironmentVariable("CENSOR_OPENVOICE_CHECKPOINTS");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return null;
    }

    private static string? ResolveOpenVoicePythonExecutable()
    {
        foreach (var candidate in EnumeratePythonCandidates())
        {
            if (CanImportOpenVoice(candidate))
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
            Environment.GetEnvironmentVariable("CENSOR_OPENVOICE_PYTHON"),
            Environment.GetEnvironmentVariable("CENSOR_MEDIA_PYTHON"),
            Environment.GetEnvironmentVariable("PYTHON"),
        })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                yield return candidate;
            }
        }

        var openVoicePython = AsrBridgeService.ResolvePythonExecutable("openvoice");
        if (!string.IsNullOrWhiteSpace(openVoicePython))
        {
            yield return openVoicePython;
        }
    }

    private static bool CanImportOpenVoice(string candidate)
    {
        try
        {
            ToolRunner.RunCaptureAsync(
                candidate,
                TimeSpan.FromSeconds(30),
                "-c",
                "import openvoice; from melo.api import TTS").GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<ReplacePrototypeManifest> LoadManifestAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;

        var manifest = new ReplacePrototypeManifest
        {
            ReplacementText = root.TryGetProperty("replacement_text", out var replacementTextElement)
                ? replacementTextElement.GetString() ?? string.Empty
                : string.Empty,
            SelectedMatch = root.TryGetProperty("selected_match", out var selectedMatchElement)
                ? selectedMatchElement.Deserialize<ProfanityMatch>() ?? new ProfanityMatch()
                : new ProfanityMatch(),
            ReferenceClip = root.TryGetProperty("reference_clip", out var referenceClipElement)
                ? referenceClipElement.Deserialize<ManifestClip>() ?? new ManifestClip()
                : new ManifestClip(),
            TargetClip = root.TryGetProperty("target_clip", out var targetClipElement)
                ? targetClipElement.Deserialize<ManifestClip>() ?? new ManifestClip()
                : new ManifestClip(),
            BackgroundStemPath = root.TryGetProperty("background_stem", out var backgroundStemElement)
                ? backgroundStemElement.GetString() ?? string.Empty
                : string.Empty,
        };

        if (root.TryGetProperty("background_clip", out var backgroundClipElement))
        {
            manifest.BackgroundClip = backgroundClipElement.ValueKind switch
            {
                JsonValueKind.String => new ManifestClip { Path = backgroundClipElement.GetString() ?? string.Empty },
                JsonValueKind.Object => backgroundClipElement.Deserialize<ManifestClip>() ?? new ManifestClip(),
                _ => new ManifestClip(),
            };
        }

        return manifest;
    }

    private sealed class ReplacePrototypeManifest
    {
        [JsonPropertyName("replacement_text")]
        public string ReplacementText { get; set; } = string.Empty;

        [JsonPropertyName("selected_match")]
        public ProfanityMatch SelectedMatch { get; set; } = new();

        [JsonPropertyName("reference_clip")]
        public ManifestClip ReferenceClip { get; set; } = new();

        [JsonPropertyName("target_clip")]
        public ManifestClip TargetClip { get; set; } = new();

        [JsonPropertyName("background_stem")]
        public string BackgroundStemPath { get; set; } = string.Empty;

        [JsonPropertyName("background_clip")]
        public ManifestClip BackgroundClip { get; set; } = new();
    }

    private sealed class ManifestClip
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("start")]
        public double Start { get; set; }

        [JsonPropertyName("end")]
        public double End { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}