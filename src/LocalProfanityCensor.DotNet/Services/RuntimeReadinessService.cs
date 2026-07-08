using LocalProfanityCensor.DotNet.Models;

namespace LocalProfanityCensor.DotNet.Services;

internal static class RuntimeReadinessService
{
    private static readonly string? CosyVoiceRepoPath = Environment.GetEnvironmentVariable("CENSOR_COSYVOICE_REPO");
    private static readonly string? CosyVoiceMatchaPath = string.IsNullOrWhiteSpace(CosyVoiceRepoPath)
        ? null
        : Path.Combine(CosyVoiceRepoPath, "third_party", "Matcha-TTS");

    public static RuntimeReadinessResult Check(AppConfig config, string scope)
    {
        var normalizedScope = NormalizeScope(scope, config);
        var result = new RuntimeReadinessResult
        {
            Scope = normalizedScope,
            BootstrapScript = ResolveBootstrapScriptPath(),
        };

        CheckCore(result);

        if (RequiresAi(normalizedScope))
        {
            CheckAi(config, result);
        }

        if (RequiresReplace(normalizedScope))
        {
            CheckReplace(config, result);
        }

        result.IsReady = result.MissingItems.Count == 0;
        result.Message = result.IsReady
            ? $"Runtime is ready for scope '{normalizedScope}'."
            : $"Runtime is not ready for scope '{normalizedScope}'.";
        return result;
    }

    private static void CheckCore(RuntimeReadinessResult result)
    {
        if (!ToolExists("ffmpeg"))
        {
            result.MissingItems.Add("ffmpeg executable");
        }

        if (!ToolExists("ffprobe"))
        {
            result.MissingItems.Add("ffprobe executable");
        }
    }

    private static void CheckAi(AppConfig config, RuntimeReadinessResult result)
    {
        var fasterWhisperPython = AsrBridgeService.ResolvePythonExecutable("faster_whisper");
        if (string.IsNullOrWhiteSpace(fasterWhisperPython))
        {
            result.MissingItems.Add("Python with faster_whisper");
        }

        if (string.Equals(config.Transcription.DialogIsolation, "demucs", StringComparison.OrdinalIgnoreCase))
        {
            var demucsPython = AsrBridgeService.ResolvePythonExecutable("demucs");
            if (string.IsNullOrWhiteSpace(demucsPython))
            {
                result.MissingItems.Add("Python with demucs");
            }
        }
    }

    private static void CheckReplace(AppConfig config, RuntimeReadinessResult result)
    {
        var replaceEngine = (config.Censor.ReplaceEngine ?? "openvoice").Trim().ToLowerInvariant();
        if (replaceEngine == "cosyvoice")
        {
            var cosyVoicePython = ResolveCosyVoicePythonExecutable();
            if (string.IsNullOrWhiteSpace(cosyVoicePython))
            {
                result.MissingItems.Add("Python with CosyVoice");
            }

            var modelDir = Environment.GetEnvironmentVariable("CENSOR_COSYVOICE_MODEL_DIR");
            if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir))
            {
                result.MissingItems.Add("CosyVoice model directory (CENSOR_COSYVOICE_MODEL_DIR)");
            }

            return;
        }

        var openVoicePython = ResolveOpenVoicePythonExecutable();
        if (string.IsNullOrWhiteSpace(openVoicePython))
        {
            result.MissingItems.Add("Python with OpenVoice and MeloTTS");
        }

        var checkpointsDir = Environment.GetEnvironmentVariable("CENSOR_OPENVOICE_CHECKPOINTS");
        if (string.IsNullOrWhiteSpace(checkpointsDir) || !Directory.Exists(checkpointsDir))
        {
            result.MissingItems.Add("OpenVoice checkpoints directory (CENSOR_OPENVOICE_CHECKPOINTS)");
        }
    }

    private static bool ToolExists(string toolName)
    {
        try
        {
            ToolRunner.EnsureToolExists(toolName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeScope(string? scope, AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(scope))
        {
            return scope.Trim().ToLowerInvariant();
        }

        return string.Equals(config.Censor.Mode, "replace", StringComparison.OrdinalIgnoreCase)
            ? "replace"
            : "ai";
    }

    private static bool RequiresAi(string scope)
    {
        return scope is "ai" or "replace";
    }

    private static bool RequiresReplace(string scope)
    {
        return string.Equals(scope, "replace", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveBootstrapScriptPath()
    {
        var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Deployment", "Scripts", "Setup-AI.ps1"));
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? ResolveOpenVoicePythonExecutable()
    {
        foreach (var candidate in new[]
        {
            Environment.GetEnvironmentVariable("CENSOR_OPENVOICE_PYTHON"),
            Environment.GetEnvironmentVariable("CENSOR_MEDIA_PYTHON"),
            Environment.GetEnvironmentVariable("PYTHON"),
        })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (CanImportOpenVoice(candidate))
            {
                return candidate;
            }
        }

        var discovered = AsrBridgeService.ResolvePythonExecutable("openvoice");
        return string.IsNullOrWhiteSpace(discovered) ? null : discovered;
    }

    private static bool CanImportOpenVoice(string candidate)
    {
        try
        {
            if (Path.IsPathRooted(candidate) && !File.Exists(candidate))
            {
                return false;
            }

            if (!Path.IsPathRooted(candidate))
            {
                ToolRunner.EnsureToolExists(candidate);
            }

            ToolRunner.RunCaptureAsync(
                candidate,
                AsrBridgeService.BuildBridgeEnvironment(),
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

    private static string? ResolveCosyVoicePythonExecutable()
    {
        foreach (var candidate in new[]
        {
            Environment.GetEnvironmentVariable("CENSOR_COSYVOICE_PYTHON"),
            Environment.GetEnvironmentVariable("CENSOR_MEDIA_PYTHON"),
            Environment.GetEnvironmentVariable("PYTHON"),
        })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (CanImportCosyVoice(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool CanImportCosyVoice(string candidate)
    {
        try
        {
            if (Path.IsPathRooted(candidate) && !File.Exists(candidate))
            {
                return false;
            }

            if (!Path.IsPathRooted(candidate))
            {
                ToolRunner.EnsureToolExists(candidate);
            }

            var environmentVariables = new Dictionary<string, string?>(AsrBridgeService.BuildBridgeEnvironment(), StringComparer.OrdinalIgnoreCase);
            var pythonPathEntries = new List<string>();
            if (Directory.Exists(CosyVoiceRepoPath))
            {
                pythonPathEntries.Add(CosyVoiceRepoPath);
            }

            if (Directory.Exists(CosyVoiceMatchaPath))
            {
                pythonPathEntries.Add(CosyVoiceMatchaPath);
            }

            var existingPythonPath = Environment.GetEnvironmentVariable("PYTHONPATH");
            if (!string.IsNullOrWhiteSpace(existingPythonPath))
            {
                pythonPathEntries.Add(existingPythonPath);
            }

            if (pythonPathEntries.Count > 0)
            {
                environmentVariables["PYTHONPATH"] = string.Join(Path.PathSeparator, pythonPathEntries);
            }

            ToolRunner.RunCaptureAsync(
                candidate,
                environmentVariables,
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
}