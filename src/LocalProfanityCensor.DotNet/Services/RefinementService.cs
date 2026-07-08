using LocalProfanityCensor.DotNet.Models;

namespace LocalProfanityCensor.DotNet.Services;

internal static class RefinementService
{
    public static Task<RefinementResult> RefineAsync(
        string inputPath,
        TranscriptResult transcript,
        List<ProfanityMatch> coarseMatches,
        ProfanityDictionary dictionary,
        AppConfig config,
        string workDir)
    {
        if (!string.Equals(config.Transcription.Engine, "caption-refine-whisper", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new RefinementResult
            {
                Strategy = "caption-only",
                Matches = coarseMatches,
            });
        }

        if (coarseMatches.Count == 0)
        {
            return Task.FromResult(new RefinementResult
            {
                Strategy = "caption-refine-whisper",
                Matches = coarseMatches,
            });
        }

        var (leadPaddingSeconds, trailPaddingSeconds) = ResolveRefinementWindowPaddingSeconds(config);
        var windows = BuildRefinementWindows(
            transcript.Segments,
            coarseMatches,
            leadPaddingSeconds,
            trailPaddingSeconds,
            ResolveMaximumWindowDurationSeconds(config));
        var warnings = new List<string>();

        if (windows.Count == 0)
        {
            return Task.FromResult(new RefinementResult
            {
                Strategy = "caption-refine-whisper",
                Matches = coarseMatches,
                Windows = windows,
            });
        }

        return RefineWithFasterWhisperAsync(inputPath, windows, coarseMatches, dictionary, config, workDir, warnings);
    }

    private static async Task<RefinementResult> RefineWithFasterWhisperAsync(
        string inputPath,
        List<RefinementWindow> windows,
        List<ProfanityMatch> coarseMatches,
        ProfanityDictionary dictionary,
        AppConfig config,
        string workDir,
        List<string> warnings)
    {
        var asrWorkDir = Path.Combine(workDir, "asr_refine");
        Directory.CreateDirectory(asrWorkDir);
        var refinedMatches = new List<ProfanityMatch>();
        var usedAsr = false;
        var extractedWindows = new List<ExtractedRefinementWindow>();

        for (var index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            window.Passes.Add(new RefinementPassEvidence
            {
                Engine = "caption-window",
                Status = "baseline",
                Segments = window.Segments,
                Matches = window.CoarseMatches,
            });

            try
            {
                var audioPath = await ExtractWindowAudioAsync(inputPath, window, asrWorkDir, index + 1, config);
                extractedWindows.Add(new ExtractedRefinementWindow
                {
                    Window = window,
                    AudioPath = audioPath,
                    Request = new AsrBridgeService.BatchBridgeRequest
                    {
                        Key = $"window_{index + 1:0000}",
                        AudioPath = audioPath,
                        AbsoluteStart = window.Start,
                    },
                });
            }
            catch (Exception ex)
            {
                refinedMatches.AddRange(window.CoarseMatches);
                warnings.Add($"ASR refinement failed for {window.Start:F3}-{window.End:F3}s; keeping caption timings. {ex.Message}");
                window.Passes.Add(new RefinementPassEvidence
                {
                    Engine = "faster-whisper",
                    Status = "failed",
                    Warnings = [ex.Message],
                });
            }
        }

        var batchResults = await AsrBridgeService.RunFasterWhisperBatchAsync(
            extractedWindows.Select(item => item.Request).ToList(),
            config,
            Path.Combine(asrWorkDir, "batch-manifest.json"));

        foreach (var extractedWindow in extractedWindows)
        {
            var window = extractedWindow.Window;
            var asrPass = batchResults[extractedWindow.Request.Key];
            asrPass.Matches = ProfanityMatcher.DetectProfanity(asrPass.Segments, dictionary);
            window.Passes.Add(asrPass);
            var selectedPass = asrPass;

            try
            {
                if (string.Equals(asrPass.Status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    usedAsr = true;
                }

                if (string.Equals(asrPass.Status, "completed", StringComparison.OrdinalIgnoreCase)
                    && ShouldTryHardWindowFallback(asrPass.Matches, config))
                {
                    var whisperxPass = await AsrBridgeService.RunWhisperXPassAsync(extractedWindow.AudioPath, window.Start, config);
                    whisperxPass.Matches = ProfanityMatcher.DetectProfanity(whisperxPass.Segments, dictionary);
                    window.Passes.Add(whisperxPass);

                    if (string.Equals(whisperxPass.Status, "completed", StringComparison.OrdinalIgnoreCase))
                    {
                        usedAsr = true;
                        if (whisperxPass.Matches.Count > 0)
                        {
                            selectedPass = whisperxPass;
                        }
                        else if (asrPass.Matches.Count == 0)
                        {
                            whisperxPass.Warnings.Add($"WhisperX fallback also found no profane word in {window.Start:F3}-{window.End:F3}s.");
                        }
                    }
                }

                if (selectedPass.Matches.Count > 0)
                {
                    refinedMatches.AddRange(selectedPass.Matches);
                }
                else
                {
                    refinedMatches.AddRange(window.CoarseMatches);
                    if (string.Equals(asrPass.Status, "completed", StringComparison.OrdinalIgnoreCase))
                    {
                        warnings.Add($"ASR refinement found no profane word in {window.Start:F3}-{window.End:F3}s; keeping caption timings.");
                    }
                }

                warnings.AddRange(window.Passes.SelectMany(pass => pass.Warnings));
            }
            catch (Exception ex)
            {
                refinedMatches.AddRange(window.CoarseMatches);
                warnings.Add($"ASR refinement failed for {window.Start:F3}-{window.End:F3}s; keeping caption timings. {ex.Message}");
                window.Passes.Add(new RefinementPassEvidence
                {
                    Engine = "faster-whisper",
                    Status = "failed",
                    Warnings = [ex.Message],
                });
            }
        }

        var finalMatches = refinedMatches.Count > 0 ? DeduplicateMatches(refinedMatches) : coarseMatches;
        return new RefinementResult
        {
            Strategy = "caption-refine-whisper",
            UsedAsr = usedAsr,
            Matches = finalMatches,
            Windows = windows,
            Warnings = warnings,
        };
    }

    private static List<RefinementWindow> BuildRefinementWindows(
        List<TranscriptSegment> segments,
        List<ProfanityMatch> coarseMatches,
        double leadPaddingSeconds,
        double trailPaddingSeconds,
        double maxWindowDurationSeconds)
    {
        var seedWindows = new List<RefinementWindow>();
        foreach (var coarseMatch in coarseMatches.OrderBy(item => item.Start).ThenBy(item => item.End))
        {
            var start = Math.Max(0.0, coarseMatch.Start - leadPaddingSeconds);
            var end = Math.Max(start + 0.05, coarseMatch.End + trailPaddingSeconds);

            if (seedWindows.Count == 0)
            {
                seedWindows.Add(new RefinementWindow
                {
                    Start = start,
                    End = end,
                    CoarseMatches = [coarseMatch],
                });
                continue;
            }

            var previous = seedWindows[^1];
            var mergedStart = Math.Min(previous.Start, start);
            var mergedEnd = Math.Max(previous.End, end);
            var mergedDuration = mergedEnd - mergedStart;
            if (start <= previous.End && mergedDuration <= maxWindowDurationSeconds)
            {
                previous.End = mergedEnd;
                previous.CoarseMatches.Add(coarseMatch);
                continue;
            }

            seedWindows.Add(new RefinementWindow
            {
                Start = start,
                End = end,
                CoarseMatches = [coarseMatch],
            });
        }

        foreach (var window in seedWindows)
        {
            window.CoarseMatches = window.CoarseMatches
                .OrderBy(item => item.Start)
                .ThenBy(item => item.End)
                .ThenBy(item => item.TermId, StringComparer.Ordinal)
                .GroupBy(item => (item.TermId, item.Start, item.End, item.NormalizedText, item.Source))
                .Select(group => group.First())
                .ToList();

            window.Segments = segments
                .Where(segment => RangesOverlap(segment.Start, segment.End, window.Start, window.End))
                .ToList();
        }

        return seedWindows;
    }

    private static (double LeadPaddingSeconds, double TrailPaddingSeconds) ResolveRefinementWindowPaddingSeconds(AppConfig config)
    {
        var defaultPaddingSeconds = config.Transcription.RefineWindowPaddingMs / 1000.0;
        var leadPaddingSeconds = config.Transcription.RefineWindowLeadMs.HasValue
            ? config.Transcription.RefineWindowLeadMs.Value / 1000.0
            : defaultPaddingSeconds;
        var trailPaddingSeconds = config.Transcription.RefineWindowTrailMs.HasValue
            ? config.Transcription.RefineWindowTrailMs.Value / 1000.0
            : defaultPaddingSeconds;
        return (leadPaddingSeconds, trailPaddingSeconds);
    }

    private static double ResolveMaximumWindowDurationSeconds(AppConfig config)
    {
        return Math.Max(0.5, config.Transcription.RefineWindowMaxDurationMs / 1000.0);
    }

    private static bool RangesOverlap(double leftStart, double leftEnd, double rightStart, double rightEnd)
    {
        return leftStart <= rightEnd && rightStart <= leftEnd;
    }

    private static async Task<string> ExtractWindowAudioAsync(string inputPath, RefinementWindow window, string outputDir, int windowIndex, AppConfig config)
    {
        ToolRunner.EnsureToolExists("ffmpeg");
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, $"window_{windowIndex:0000}.wav");
        var refinementSourcePath = ResolveRefinementSourcePath(inputPath, config);
        var sampleRate = string.Equals(config.Transcription.DialogIsolation, "deepfilternet", StringComparison.OrdinalIgnoreCase) ? "48000" : "16000";
        var command = new List<string>
        {
            "-y",
            "-ss",
            window.Start.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
            "-to",
            window.End.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
            "-i",
            refinementSourcePath,
            "-vn",
            "-ac",
            "1",
            "-ar",
            sampleRate,
        };

        if (string.Equals(config.Transcription.DialogIsolation, "ffmpeg-dialog", StringComparison.OrdinalIgnoreCase)
            || string.Equals(config.Transcription.DialogIsolation, "demucs", StringComparison.OrdinalIgnoreCase))
        {
            command.AddRange(["-af", "highpass=f=120,lowpass=f=3800"]);
        }

        command.Add(outputPath);
        await ToolRunner.RunAsync("ffmpeg", [.. command]);
        return outputPath;
    }

    private static string ResolveRefinementSourcePath(string inputPath, AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Transcription.FullAudioSourcePath))
        {
            var configuredPath = Path.GetFullPath(config.Transcription.FullAudioSourcePath);
            if (File.Exists(configuredPath))
            {
                return configuredPath;
            }
        }

        return inputPath;
    }

    private sealed class ExtractedRefinementWindow
    {
        public RefinementWindow Window { get; set; } = new();

        public string AudioPath { get; set; } = string.Empty;

        public AsrBridgeService.BatchBridgeRequest Request { get; set; } = new();
    }

    private static List<ProfanityMatch> DeduplicateMatches(List<ProfanityMatch> matches)
    {
        return matches
            .OrderBy(item => item.Start)
            .ThenBy(item => item.End)
            .ThenBy(item => item.TermId, StringComparer.Ordinal)
            .GroupBy(item => (item.TermId, item.Start, item.End, item.NormalizedText, item.Source))
            .Select(group => group.First())
            .ToList();
    }

    private static bool ShouldTryHardWindowFallback(List<ProfanityMatch> matches, AppConfig config)
    {
        if (!string.Equals(config.Transcription.HardWindowFallbackEngine, "whisperx", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (matches.Count == 0)
        {
            return true;
        }

        return matches.Any(match => match.Confidence.HasValue && match.Confidence.Value < config.Transcription.HardWindowConfidenceThreshold);
    }
}