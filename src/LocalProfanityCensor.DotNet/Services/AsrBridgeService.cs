using System.Text.Json;
using System.Text.Json.Serialization;
using LocalProfanityCensor.DotNet.Models;

namespace LocalProfanityCensor.DotNet.Services;

internal static class AsrBridgeService
{
    private const double MinimumWordDurationSeconds = 0.01;
    private static readonly TimeSpan BridgeCommandTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FullAudioBridgeCommandTimeout = TimeSpan.FromHours(4);
    private static readonly TimeSpan PythonProbeTimeout = TimeSpan.FromSeconds(20);
    private static readonly object PythonExecutableCacheSync = new();
    private static readonly Dictionary<string, string> PythonExecutableCache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<TranscriptResult?> TranscribeFullAudioAsync(string inputPath, MediaInfo mediaInfo, AppConfig config, string workDir)
    {
        if (mediaInfo.AudioStreams.Count == 0)
        {
            return null;
        }

        var preprocessingWarnings = new List<string>();
        var audioPath = await ResolveFullAudioSourcePathAsync(inputPath, config, workDir, preprocessingWarnings);
        var preparedAudioPath = await PrepareFullAudioForAsrAsync(audioPath, config, workDir, preprocessingWarnings);

        ProgressReporter.Report("Running faster-whisper on full audio");
        var pass = await RunFasterWhisperPassAsync(preparedAudioPath, 0.0, config, FullAudioBridgeCommandTimeout);
        if (string.Equals(pass.Status, "completed", StringComparison.OrdinalIgnoreCase) && pass.Segments.Count > 0)
        {
            return new TranscriptResult
            {
                Source = "full_audio_asr",
                Segments = pass.Segments,
                Warnings = [.. preprocessingWarnings, .. BuildTranscriptWarnings(pass)],
            };
        }

        var warnings = new List<string>(preprocessingWarnings);
        warnings.AddRange(BuildTranscriptWarnings(pass));
        if (!ShouldTryWhisperXFullAudioFallback(config, pass))
        {
            return new TranscriptResult
            {
                Source = "full_audio_asr",
                Warnings = warnings,
            };
        }

        ProgressReporter.Report("Running WhisperX full-audio fallback");
        var whisperxPass = await RunWhisperXPassAsync(preparedAudioPath, 0.0, config, FullAudioBridgeCommandTimeout);
        warnings.AddRange(BuildTranscriptWarnings(whisperxPass));
        return new TranscriptResult
        {
            Source = "full_audio_asr",
            Segments = whisperxPass.Segments,
            Warnings = warnings,
        };
    }

    private static async Task<string> ResolveFullAudioSourcePathAsync(string inputPath, AppConfig config, string workDir, List<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(config.Transcription.FullAudioSourcePath))
        {
            var configuredPath = Path.GetFullPath(config.Transcription.FullAudioSourcePath);
            if (!File.Exists(configuredPath))
            {
                warnings.Add($"Configured full-audio ASR source was not found: {configuredPath}. Falling back to extracted audio from the input media.");
            }
            else
            {
                ProgressReporter.Report($"Using configured full-audio ASR source: {Path.GetFileName(configuredPath)}");
                return configuredPath;
            }
        }

        var audioPath = Path.Combine(workDir, "full_audio.wav");
        ProgressReporter.Report("Extracting full-audio ASR source track");
        await MediaRenderService.ExtractAudioAsync(inputPath, audioPath);
        return audioPath;
    }

    private static async Task<string> PrepareFullAudioForAsrAsync(string audioPath, AppConfig config, string workDir, List<string> warnings)
    {
        if (string.Equals(config.Transcription.DialogIsolation, "demucs", StringComparison.OrdinalIgnoreCase))
        {
            return await PrepareFullAudioWithDemucsAsync(audioPath, config, workDir, warnings);
        }

        if (!string.Equals(config.Transcription.DialogIsolation, "deepfilternet", StringComparison.OrdinalIgnoreCase))
        {
            return audioPath;
        }

        var pythonExecutable = ResolvePythonExecutable("df");
        if (pythonExecutable is null)
        {
            warnings.Add("DeepFilterNet full-audio preprocessing was requested, but no Python executable with the `df` module was found. Continuing with raw full audio.");
            return audioPath;
        }

        var deepFilterInputPath = Path.Combine(workDir, "full_audio.deepfilternet_input.wav");
        var deepFilterOutputDir = Path.Combine(workDir, "deepfilternet");
        var deepFilterOutputPath = Path.Combine(workDir, "full_audio.deepfilternet.wav");
        Directory.CreateDirectory(deepFilterOutputDir);

        try
        {
            ProgressReporter.Report("Preparing mono 48 kHz source for DeepFilterNet full-audio preprocessing");
            await ToolRunner.RunAsync(
                "ffmpeg",
                new ToolRunner.CommandProgressInfo("preprocess", "Preparing DeepFilterNet input", null, Path.GetFileName(audioPath), config.Censor.Mode),
                "-y",
                "-i",
                audioPath,
                "-ac",
                "1",
                "-ar",
                "48000",
                deepFilterInputPath);

            if (File.Exists(deepFilterOutputPath))
            {
                File.Delete(deepFilterOutputPath);
            }

            var environmentVariables = BuildBridgeEnvironment();
            var deepFilterArguments = new List<string>
            {
                "-m",
                "df.enhance",
                "--output-dir",
                deepFilterOutputDir,
                "--no-suffix",
                "--no-delay-compensation",
            };

            if (!string.IsNullOrWhiteSpace(config.Transcription.DeepfilternetModel))
            {
                deepFilterArguments.AddRange(["--model-base-dir", config.Transcription.DeepfilternetModel]);
            }

            if (config.Transcription.DeepfilternetPostFilter)
            {
                deepFilterArguments.Add("--pf");
            }

            deepFilterArguments.Add(deepFilterInputPath);

            ProgressReporter.Report("Running DeepFilterNet full-audio preprocessing");
            await ToolRunner.RunCaptureAsync(
                pythonExecutable,
                environmentVariables,
                BridgeCommandTimeout,
                new ToolRunner.CommandProgressInfo("preprocess", "Running DeepFilterNet", "full-audio preprocessing", Path.GetFileName(audioPath), config.Censor.Mode),
                [.. deepFilterArguments]);

            var generatedOutputPath = Path.Combine(deepFilterOutputDir, Path.GetFileName(deepFilterInputPath));
            if (!File.Exists(generatedOutputPath))
            {
                warnings.Add("DeepFilterNet full-audio preprocessing completed without producing an output file. Continuing with raw full audio.");
                return audioPath;
            }

            File.Move(generatedOutputPath, deepFilterOutputPath, true);
            ProgressReporter.Report($"DeepFilterNet full-audio preprocessing complete: {Path.GetFileName(deepFilterOutputPath)}");
            return deepFilterOutputPath;
        }
        catch (Exception ex)
        {
            warnings.Add($"DeepFilterNet full-audio preprocessing failed: {ex.Message}. Continuing with raw full audio.");
            ProgressReporter.Report($"DeepFilterNet full-audio preprocessing failed; continuing with raw audio: {ex.Message}");
            return audioPath;
        }
    }

    private static async Task<string> PrepareFullAudioWithDemucsAsync(string audioPath, AppConfig config, string workDir, List<string> warnings)
    {
        var pythonExecutable = ResolvePythonExecutable("demucs");
        if (pythonExecutable is null)
        {
            warnings.Add("Demucs full-audio preprocessing was requested, but no Python executable with the `demucs` module was found. Continuing with raw full audio.");
            return audioPath;
        }

        var demucsInputPath = Path.Combine(workDir, "full_audio.demucs_input.wav");
        var demucsOutputDir = Path.Combine(workDir, "demucs");
        var demucsOutputPath = Path.Combine(workDir, "full_audio.demucs.vocals.wav");
        Directory.CreateDirectory(demucsOutputDir);

        try
        {
            ProgressReporter.Report("Preparing stereo 44.1 kHz source for Demucs full-audio preprocessing");
            await ToolRunner.RunAsync(
                "ffmpeg",
                new ToolRunner.CommandProgressInfo("preprocess", "Preparing Demucs input", null, Path.GetFileName(audioPath), config.Censor.Mode),
                "-y",
                "-i",
                audioPath,
                "-ac",
                "2",
                "-ar",
                "44100",
                demucsInputPath);

            if (File.Exists(demucsOutputPath))
            {
                File.Delete(demucsOutputPath);
            }

            var environmentVariables = BuildBridgeEnvironment();
            var resolvedDevice = await ResolveDemucsDeviceAsync(pythonExecutable, environmentVariables, config);
            var resolvedModel = ResolveDemucsModel(config);
            var demucsArguments = new List<string>
            {
                "-m",
                "demucs.separate",
                "-n",
                resolvedModel,
                "--two-stems",
                "vocals",
                "-d",
                resolvedDevice,
                "-o",
                demucsOutputDir,
                demucsInputPath,
            };

            ProgressReporter.Report($"Running Demucs full-audio preprocessing with model {resolvedModel} on {resolvedDevice}");
            await ToolRunner.RunCaptureAsync(
                pythonExecutable,
                environmentVariables,
                TimeSpan.FromHours(2),
                new ToolRunner.CommandProgressInfo("preprocess", "Running Demucs", $"model {resolvedModel} on {resolvedDevice}", Path.GetFileName(audioPath), config.Censor.Mode),
                [.. demucsArguments]);

            var generatedOutputPath = Path.Combine(
                demucsOutputDir,
                resolvedModel,
                Path.GetFileNameWithoutExtension(demucsInputPath),
                "vocals.wav");

            if (!File.Exists(generatedOutputPath))
            {
                warnings.Add("Demucs full-audio preprocessing completed without producing a vocals stem. Continuing with raw full audio.");
                return audioPath;
            }

            File.Move(generatedOutputPath, demucsOutputPath, true);
            ProgressReporter.Report($"Demucs full-audio preprocessing complete: {Path.GetFileName(demucsOutputPath)}");
            return demucsOutputPath;
        }
        catch (Exception ex)
        {
            warnings.Add($"Demucs full-audio preprocessing failed: {ex.Message}. Continuing with raw full audio.");
            ProgressReporter.Report($"Demucs full-audio preprocessing failed; continuing with raw audio: {ex.Message}");
            return audioPath;
        }
    }

    public static async Task<RefinementPassEvidence> RunFasterWhisperPassAsync(
        string audioPath,
        double absoluteStart,
        AppConfig config,
        TimeSpan? timeoutOverride = null)
    {
        return await RunBridgePassAsync(
            "faster-whisper",
            audioPath,
            absoluteStart,
            config,
            config.Transcription.Device,
            timeoutOverride ?? BridgeCommandTimeout);
    }

    public static async Task<RefinementPassEvidence> RunWhisperXPassAsync(
        string audioPath,
        double absoluteStart,
        AppConfig config,
        TimeSpan? timeoutOverride = null)
    {
        return await RunBridgePassAsync(
            "whisperx",
            audioPath,
            absoluteStart,
            config,
            config.Transcription.HardWindowFallbackDevice,
            timeoutOverride ?? BridgeCommandTimeout);
    }

    public static async Task<Dictionary<string, RefinementPassEvidence>> RunFasterWhisperBatchAsync(
        IReadOnlyList<BatchBridgeRequest> requests,
        AppConfig config,
        string manifestPath,
        TimeSpan? timeoutOverride = null)
    {
        var results = requests.ToDictionary(
            request => request.Key,
            _ => new RefinementPassEvidence
            {
                Engine = "faster-whisper",
                Status = "not_run",
            },
            StringComparer.Ordinal);

        if (requests.Count == 0)
        {
            return results;
        }

        var requiredModule = "faster_whisper";
        var pythonExecutable = ResolvePythonExecutable(requiredModule);
        var bridgeScriptPath = ResolveBridgeScriptPath();

        if (pythonExecutable is null)
        {
            var warning = $"No Python executable with `{requiredModule}` was found for the faster-whisper bridge. Set CENSOR_MEDIA_PYTHON or ensure a compatible python is on PATH.";
            foreach (var request in requests)
            {
                results[request.Key] = new RefinementPassEvidence
                {
                    Engine = "faster-whisper",
                    Status = "unavailable",
                    Warnings = [warning],
                };
            }

            return results;
        }

        if (!File.Exists(bridgeScriptPath))
        {
            var warning = $"Bridge script was not found: {bridgeScriptPath}";
            foreach (var request in requests)
            {
                results[request.Key] = new RefinementPassEvidence
                {
                    Engine = "faster-whisper",
                    Status = "unavailable",
                    Warnings = [warning],
                };
            }

            return results;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? Directory.GetCurrentDirectory());
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new BatchBridgeManifest
        {
            Items = requests.Select(request => new BatchBridgeManifestItem
            {
                Key = request.Key,
                AudioPath = request.AudioPath,
            }).ToList(),
        }));

        try
        {
            var environmentVariables = BuildBridgeEnvironment();
            ProgressReporter.Report($"Starting faster-whisper batch bridge for {requests.Count} refinement windows");
            ProgressReporter.Report("Waiting for faster-whisper batch bridge process to finish");
            var commandResult = await ToolRunner.RunCaptureAsync(
                pythonExecutable,
                environmentVariables,
                timeoutOverride ?? BridgeCommandTimeout,
                new ToolRunner.CommandProgressInfo("refine", "Running faster-whisper batch bridge", $"windows {requests.Count}", null, config.Censor.Mode),
                bridgeScriptPath,
                "faster-whisper-batch",
                manifestPath,
                config.Transcription.Model,
                ResolveDevice(config.Transcription.Device),
                config.Transcription.ComputeType,
                config.Processing.Language,
                config.Transcription.WordTimestamps ? "true" : "false",
                config.Transcription.VadFilter ? "true" : "false");

            ProgressReporter.Report("faster-whisper batch bridge process exited; parsing transcript payload");
            var warnings = ExtractBridgeWarnings(commandResult.StandardError);
            var batchResults = ParseBatchBridgeResult(commandResult.StandardOutput, requests);
            foreach (var request in requests)
            {
                if (batchResults.TryGetValue(request.Key, out var bridgeResult))
                {
                    results[request.Key] = new RefinementPassEvidence
                    {
                        Engine = "faster-whisper",
                        Device = bridgeResult.Device,
                        Status = "completed",
                        Segments = bridgeResult.Segments,
                        Warnings = [.. warnings],
                    };
                }
                else
                {
                    results[request.Key] = new RefinementPassEvidence
                    {
                        Engine = "faster-whisper",
                        Status = "failed",
                        Warnings = ["Batch bridge did not return a result for this refinement window."],
                    };
                }
            }

            ProgressReporter.Report($"faster-whisper batch transcript payload parsed for {batchResults.Count} refinement windows");
            return results;
        }
        catch (Exception ex)
        {
            ProgressReporter.Report($"faster-whisper batch bridge failed: {ex.Message}");
            foreach (var request in requests)
            {
                results[request.Key] = new RefinementPassEvidence
                {
                    Engine = "faster-whisper",
                    Status = "failed",
                    Warnings = [ex.Message],
                };
            }

            return results;
        }
    }

    private static async Task<RefinementPassEvidence> RunBridgePassAsync(
        string engine,
        string audioPath,
        double absoluteStart,
        AppConfig config,
        string requestedDevice,
        TimeSpan commandTimeout)
    {
        var requiredModule = string.Equals(engine, "whisperx", StringComparison.OrdinalIgnoreCase) ? "whisperx" : "faster_whisper";
        var pythonExecutable = ResolvePythonExecutable(requiredModule);
        var bridgeScriptPath = ResolveBridgeScriptPath();

        if (pythonExecutable is null)
        {
            return new RefinementPassEvidence
            {
                Engine = engine,
                Status = "unavailable",
                Warnings = [$"No Python executable with `{requiredModule}` was found for the {engine} bridge. Set CENSOR_MEDIA_PYTHON or ensure a compatible python is on PATH."],
            };
        }

        if (!File.Exists(bridgeScriptPath))
        {
            return new RefinementPassEvidence
            {
                Engine = engine,
                Status = "unavailable",
                Warnings = [$"Bridge script was not found: {bridgeScriptPath}"],
            };
        }

        try
        {
            var environmentVariables = BuildBridgeEnvironment();
            ProgressReporter.Report($"Starting {engine} bridge on {Path.GetFileName(audioPath)}");
            ProgressReporter.Report($"Waiting for {engine} bridge process to finish");
            var commandResult = await ToolRunner.RunCaptureAsync(
                pythonExecutable,
                environmentVariables,
                commandTimeout,
                new ToolRunner.CommandProgressInfo("asr", $"Running {engine} bridge", null, Path.GetFileName(audioPath), config.Censor.Mode),
                bridgeScriptPath,
                engine,
                audioPath,
                config.Transcription.Model,
                ResolveDevice(requestedDevice),
                config.Transcription.ComputeType,
                config.Processing.Language,
                config.Transcription.WordTimestamps ? "true" : "false",
                string.Equals(engine, "faster-whisper", StringComparison.OrdinalIgnoreCase)
                    ? (config.Transcription.VadFilter ? "true" : "false")
                    : "true");

            ProgressReporter.Report($"{engine} bridge process exited; parsing transcript payload");
            var bridgeResult = ParseBridgeResult(commandResult.StandardOutput, absoluteStart);
            ProgressReporter.Report($"{engine} transcript payload parsed with {bridgeResult.Segments.Count} segment(s)");
            return new RefinementPassEvidence
            {
                Engine = engine,
                Device = bridgeResult.Device,
                Status = "completed",
                Segments = bridgeResult.Segments,
                Warnings = ExtractBridgeWarnings(commandResult.StandardError),
            };
        }
        catch (Exception ex)
        {
            ProgressReporter.Report($"{engine} bridge failed: {ex.Message}");
            return new RefinementPassEvidence
            {
                Engine = engine,
                Status = "failed",
                Warnings = [ex.Message],
            };
        }
    }

    internal static IReadOnlyDictionary<string, string?> BuildBridgeEnvironment()
    {
        var hfHome = ResolveHuggingFaceHome();
        Directory.CreateDirectory(hfHome);
        var tokenPath = Path.Combine(hfHome, "token");
        if (!File.Exists(tokenPath))
        {
            File.WriteAllText(tokenPath, string.Empty);
        }

        return new Dictionary<string, string?>
        {
            ["HF_HOME"] = hfHome,
            ["HF_HUB_CACHE"] = Path.Combine(hfHome, "hub"),
            ["HUGGINGFACE_HUB_CACHE"] = Path.Combine(hfHome, "hub"),
            ["TRANSFORMERS_CACHE"] = Path.Combine(hfHome, "transformers"),
            ["HF_TOKEN_PATH"] = tokenPath,
            ["KMP_DUPLICATE_LIB_OK"] = "TRUE",
        };
    }

    internal static string ResolveDemucsModel(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Transcription.DemucsModel))
        {
            return config.Transcription.DemucsModel.Trim();
        }

        return string.Equals(config.Transcription.DemucsProfile, "speed", StringComparison.OrdinalIgnoreCase)
            ? "htdemucs"
            : "htdemucs_ft";
    }

    internal static async Task<string> ResolveDemucsDeviceAsync(
        string pythonExecutable,
        IReadOnlyDictionary<string, string?> environmentVariables,
        AppConfig config)
    {
        var configuredDevice = (config.Transcription.DemucsDevice ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(configuredDevice))
        {
            configuredDevice = "auto";
        }

        if (!string.Equals(configuredDevice, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return configuredDevice;
        }

        if (await IsTorchCudaAvailableAsync(pythonExecutable, environmentVariables))
        {
            return "cuda";
        }

        return "cpu";
    }

    private static async Task<bool> IsTorchCudaAvailableAsync(
        string pythonExecutable,
        IReadOnlyDictionary<string, string?> environmentVariables)
    {
        try
        {
            await ToolRunner.RunCaptureAsync(
                pythonExecutable,
                environmentVariables,
                PythonProbeTimeout,
                "-c",
                "import torch; raise SystemExit(0 if torch.cuda.is_available() else 1)");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveHuggingFaceHome()
    {
        var configured = Environment.GetEnvironmentVariable("CENSOR_MEDIA_HF_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "LocalProfanityCensor", "huggingface-cache");
    }

    internal static string? ResolvePythonExecutable(string requiredModule)
    {
        lock (PythonExecutableCacheSync)
        {
            if (PythonExecutableCache.TryGetValue(requiredModule, out var cachedCandidate))
            {
                return string.IsNullOrWhiteSpace(cachedCandidate) ? null : cachedCandidate;
            }
        }

        var seenCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in FilterUniqueCandidates(EnumerateExplicitPythonCandidates(), seenCandidates))
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (CanImportModule(candidate, requiredModule))
            {
                CachePythonExecutable(requiredModule, candidate);
                return candidate;
            }
        }

        foreach (var candidate in FilterUniqueCandidates(EnumerateLocalVirtualEnvironmentCandidates(), seenCandidates))
        {
            if (CanImportModule(candidate, requiredModule))
            {
                CachePythonExecutable(requiredModule, candidate);
                return candidate;
            }
        }

        foreach (var candidate in FilterUniqueCandidates(EnumerateFallbackPythonCandidates(), seenCandidates))
        {
            if (CanImportModule(candidate, requiredModule))
            {
                CachePythonExecutable(requiredModule, candidate);
                return candidate;
            }
        }

        CachePythonExecutable(requiredModule, string.Empty);
        return null;
    }

    private static void CachePythonExecutable(string requiredModule, string candidate)
    {
        lock (PythonExecutableCacheSync)
        {
            PythonExecutableCache[requiredModule] = candidate;
        }
    }

    private static IEnumerable<string?> EnumerateExplicitPythonCandidates()
    {
        foreach (var candidate in new[]
        {
            Environment.GetEnvironmentVariable("CENSOR_MEDIA_PYTHON"),
            Environment.GetEnvironmentVariable("PYTHON"),
        })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (Path.IsPathRooted(candidate) || candidate.Contains(Path.DirectorySeparatorChar) || candidate.Contains(Path.AltDirectorySeparatorChar))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> EnumerateLocalVirtualEnvironmentCandidates()
    {
        foreach (var baseDirectory in ResolveSearchRoots())
        {
            foreach (var currentDirectory in EnumerateSelfAndParents(baseDirectory))
            {
                yield return Path.Combine(currentDirectory, ".venv", "Scripts", "python.exe");
                yield return Path.Combine(currentDirectory, "venv", "Scripts", "python.exe");
            }
        }
    }

    private static IEnumerable<string> EnumerateFallbackPythonCandidates()
    {
        foreach (var candidate in new[]
        {
            Environment.GetEnvironmentVariable("CENSOR_MEDIA_PYTHON"),
            Environment.GetEnvironmentVariable("PYTHON"),
            "python",
            "py",
        })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> FilterUniqueCandidates(IEnumerable<string?> candidates, HashSet<string> seenCandidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var normalizedCandidate = candidate;
            if ((candidate.Contains(Path.DirectorySeparatorChar) || candidate.Contains(Path.AltDirectorySeparatorChar))
                && Path.IsPathFullyQualified(candidate))
            {
                normalizedCandidate = Path.GetFullPath(candidate);
            }

            if (seenCandidates.Add(normalizedCandidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> ResolveSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[]
        {
            Environment.CurrentDirectory,
            AppContext.BaseDirectory,
        })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(root);
            if (seen.Add(fullPath))
            {
                yield return fullPath;
            }
        }
    }

    private static IEnumerable<string> EnumerateSelfAndParents(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            yield return current.FullName;
            current = current.Parent;
        }
    }

    private static bool CanImportModule(string candidate, string requiredModule)
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

            ProgressReporter.Report($"Probing python candidate {candidate} for module {requiredModule}");
            ToolRunner.RunCaptureAsync(candidate, PythonProbeTimeout, "-c", $"import {requiredModule}").GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveBridgeScriptPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "Assets", "faster_whisper_bridge.py");
    }

    private static string ResolveDevice(string device)
    {
        return string.IsNullOrWhiteSpace(device) ? "auto" : device.Trim().ToLowerInvariant();
    }

    private static bool ShouldTryWhisperXFullAudioFallback(AppConfig config, RefinementPassEvidence fasterWhisperPass)
    {
        return string.Equals(config.Transcription.HardWindowFallbackEngine, "whisperx", StringComparison.OrdinalIgnoreCase)
            && (!string.Equals(fasterWhisperPass.Status, "completed", StringComparison.OrdinalIgnoreCase) || fasterWhisperPass.Segments.Count == 0);
    }

    private static List<string> BuildTranscriptWarnings(RefinementPassEvidence pass)
    {
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(pass.Device))
        {
            warnings.Add($"ASR device: {pass.Engine}:{pass.Device}");
        }

        if (!string.IsNullOrWhiteSpace(pass.Status) && !string.Equals(pass.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"ASR pass status: {pass.Engine}:{pass.Status}");
        }

        warnings.AddRange(pass.Warnings);
        return warnings;
    }

    private static List<string> ExtractBridgeWarnings(string standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
        {
            return [];
        }

        return standardError
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !IsIgnorableBridgeWarning(line))
            .ToList();
    }

    private static bool IsIgnorableBridgeWarning(string line)
    {
        if (line.StartsWith("[bridge]", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (line.StartsWith("warnings.warn(", StringComparison.OrdinalIgnoreCase)
            || line.Contains("FutureWarning:", StringComparison.OrdinalIgnoreCase)
            || line.Contains("TRANSFORMERS_CACHE", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static BridgeResult ParseBridgeResult(string json, double absoluteStart)
    {
        using var document = JsonDocument.Parse(ExtractJsonPayload(json));
        var device = GetString(document.RootElement, "device");
        if (!document.RootElement.TryGetProperty("segments", out var segmentArray) || segmentArray.ValueKind != JsonValueKind.Array)
        {
            return new BridgeResult(device, []);
        }

        return new BridgeResult(device, ParseBridgeSegments(segmentArray, absoluteStart));
    }

    private static Dictionary<string, BridgeResult> ParseBatchBridgeResult(string json, IReadOnlyList<BatchBridgeRequest> requests)
    {
        var requestMap = requests.ToDictionary(request => request.Key, StringComparer.Ordinal);
        using var document = JsonDocument.Parse(ExtractJsonPayload(json));
        var device = GetString(document.RootElement, "device");
        var results = new Dictionary<string, BridgeResult>(StringComparer.Ordinal);
        if (!document.RootElement.TryGetProperty("results", out var resultsArray) || resultsArray.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var resultElement in resultsArray.EnumerateArray())
        {
            var key = GetString(resultElement, "key");
            if (string.IsNullOrWhiteSpace(key) || !requestMap.TryGetValue(key, out var request))
            {
                continue;
            }

            if (!resultElement.TryGetProperty("segments", out var segmentArray) || segmentArray.ValueKind != JsonValueKind.Array)
            {
                results[key] = new BridgeResult(device, []);
                continue;
            }

            results[key] = new BridgeResult(device, ParseBridgeSegments(segmentArray, request.AbsoluteStart));
        }

        return results;
    }

    private static List<TranscriptSegment> ParseBridgeSegments(JsonElement segmentArray, double absoluteStart)
    {
        var segments = new List<TranscriptSegment>();

        foreach (var segmentElement in segmentArray.EnumerateArray())
        {
            var segmentStart = absoluteStart + GetDouble(segmentElement, "start");
            var segmentEnd = absoluteStart + GetDouble(segmentElement, "end");
            var text = TextNormalization.NormalizeText(GetString(segmentElement, "text"), preserveCase: true);
            var words = new List<TranscriptWord>();
            var previousWordEnd = segmentStart;
            if (segmentElement.TryGetProperty("words", out var wordArray) && wordArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var wordElement in wordArray.EnumerateArray())
                {
                    var rawText = GetString(wordElement, "text");
                    var normalized = TextNormalization.NormalizeToken(rawText);
                    if (string.IsNullOrWhiteSpace(normalized))
                    {
                        continue;
                    }

                    var rawWordStart = absoluteStart + GetDouble(wordElement, "start");
                    var rawWordEnd = absoluteStart + GetDouble(wordElement, "end");
                    var wordStart = Math.Max(previousWordEnd, rawWordStart);
                    var wordEnd = Math.Max(wordStart + MinimumWordDurationSeconds, rawWordEnd);

                    words.Add(new TranscriptWord
                    {
                        Text = rawText,
                        Normalized = normalized,
                        Start = wordStart,
                        End = wordEnd,
                        Confidence = GetNullableDouble(wordElement, "confidence"),
                        Source = "asr",
                        TimingSource = "asr_word_timestamps",
                        AlignmentSource = "bridge_word_timestamps",
                    });

                    previousWordEnd = wordEnd;
                }
            }

            if (words.Count == 0 && string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            segments.Add(new TranscriptSegment
            {
                Text = text,
                Start = segmentStart,
                End = words.Count > 0
                    ? Math.Max(words.Max(word => word.End), Math.Max(segmentStart + 0.05, segmentEnd))
                    : Math.Max(segmentStart + 0.05, segmentEnd),
                Source = "asr",
                Words = words.Count > 0 ? words : BuildFallbackWords(text, segmentStart, Math.Max(segmentStart + 0.05, segmentEnd)),
            });
        }

        return segments;
    }

    private sealed record BridgeResult(string? Device, List<TranscriptSegment> Segments);

    private static string ExtractJsonPayload(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return output;
        }

        var lines = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var line = lines[index].Trim();
            if (line.StartsWith('{') || line.StartsWith('['))
            {
                return line;
            }
        }

        return output.Trim();
    }

    private static List<TranscriptWord> BuildFallbackWords(string text, double start, double end)
    {
        var tokens = text.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !string.IsNullOrWhiteSpace(TextNormalization.NormalizeToken(token)))
            .ToList();
        var words = new List<TranscriptWord>();
        if (tokens.Count == 0)
        {
            return words;
        }

        var duration = Math.Max(0.05, end - start);
        var step = duration / tokens.Count;
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            var wordStart = start + (index * step);
            var wordEnd = index == tokens.Count - 1 ? end : Math.Min(end, wordStart + step);
            words.Add(new TranscriptWord
            {
                Text = token,
                Normalized = TextNormalization.NormalizeToken(token),
                Start = wordStart,
                End = wordEnd,
                Source = "asr",
                TimingSource = "asr_fallback_estimated",
            });
        }

        return words;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) ? property.ToString() ?? string.Empty : string.Empty;
    }

    private static double GetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0.0;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value))
        {
            return value;
        }

        return double.TryParse(property.ToString(), out var parsed) ? parsed : 0.0;
    }

    private static double? GetNullableDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value))
        {
            return value;
        }

        return double.TryParse(property.ToString(), out var parsed) ? parsed : null;
    }

    internal sealed class BatchBridgeRequest
    {
        public string Key { get; set; } = string.Empty;

        public string AudioPath { get; set; } = string.Empty;

        public double AbsoluteStart { get; set; }
    }

    private sealed class BatchBridgeManifest
    {
        [JsonPropertyName("items")]
        public List<BatchBridgeManifestItem> Items { get; set; } = [];
    }

    private sealed class BatchBridgeManifestItem
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("audio_path")]
        public string AudioPath { get; set; } = string.Empty;
    }
}