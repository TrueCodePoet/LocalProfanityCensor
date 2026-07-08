using System.Text.Json;
using LocalProfanityCensor.DotNet.Models;

namespace LocalProfanityCensor.DotNet.Services;

internal static class ReplacePrototypeService
{
    private const double TargetLeadPaddingSeconds = 0.20;
    private const double TargetTrailPaddingSeconds = 0.35;
    private const double ReferencePaddingSeconds = 0.08;
    private const double MinimumReferenceDurationSeconds = 0.80;
    private const double MaximumReferenceDurationSeconds = 6.00;

    public static async Task<ReplacePrototypeResult> PrepareAsync(
        string inputPath,
        string outputDir,
        string dictionaryPath,
        AppConfig config,
        int requestedMatchIndex)
    {
        var prototypeDir = Path.Combine(outputDir, ".replace-prototype");
        Directory.CreateDirectory(prototypeDir);

        var prototypeConfig = CloneConfig(config);
        prototypeConfig.DryRun = true;
        prototypeConfig.KeepWork = true;
        prototypeConfig.Censor.Mode = "replace";
        prototypeConfig.Subtitles.GenerateCensoredSubtitle = true;

        var analysisOutputPath = Path.Combine(prototypeDir, "analysis-placeholder.mkv");
        var processResult = await ProfanityProcessingService.ProcessFileAsync(inputPath, analysisOutputPath, dictionaryPath, prototypeConfig);

        var result = new ReplacePrototypeResult
        {
            InputFile = inputPath,
            WorkDir = prototypeDir,
            SubtitlePreviewFiles = [.. processResult.GeneratedSubtitleFiles],
            Warnings = [.. processResult.Warnings],
        };

        if (processResult.Matches.Count == 0)
        {
            result.Status = "failed";
            result.Message = "No profanity matches were found for replace-mode prototyping.";
            return result;
        }

        var replaceableMatches = processResult.Matches
            .Where(match => !string.IsNullOrWhiteSpace(match.ReplacementText))
            .OrderBy(match => match.Start)
            .ThenBy(match => match.End)
            .ToList();

        if (replaceableMatches.Count == 0)
        {
            result.Status = "failed";
            result.Message = "No matches with dictionary replacement text were found for replace-mode prototyping.";
            return result;
        }

        var selectedMatchPosition = Math.Clamp(requestedMatchIndex, 1, replaceableMatches.Count) - 1;
        var selectedMatch = replaceableMatches[selectedMatchPosition];
        var selectedWindow = processResult.Refinement?.Windows
            .FirstOrDefault(window => window.Start <= selectedMatch.Start && window.End >= selectedMatch.End);

        if (selectedWindow is null)
        {
            result.Status = "failed";
            result.Message = "Unable to locate the refinement window for the selected replacement match.";
            return result;
        }

        var stemArtifacts = await EnsureDemucsStemsAsync(inputPath, prototypeDir, prototypeConfig);
        result.Artifacts.ExtractedAudio = stemArtifacts.ExtractedAudio;
        result.Artifacts.VocalStem = stemArtifacts.VocalStem;
        result.Artifacts.BackgroundStem = stemArtifacts.BackgroundStem;

        var clipsDir = Path.Combine(prototypeDir, "clips");
        Directory.CreateDirectory(clipsDir);

        var targetClip = new ReplacePrototypeClip
        {
            Path = Path.Combine(clipsDir, "target.vocals.wav"),
            Start = Math.Max(0.0, selectedMatch.Start - TargetLeadPaddingSeconds),
            End = Math.Max(selectedMatch.End + TargetTrailPaddingSeconds, selectedMatch.Start + 0.30),
            Text = selectedMatch.MatchedText,
        };

        var referenceClip = SelectReferenceClip(selectedMatch, selectedWindow, processResult.Matches, targetClip);
        if (referenceClip is null)
        {
            result.Status = "failed";
            result.SelectedMatchIndex = selectedMatchPosition + 1;
            result.SelectedMatch = selectedMatch;
            result.SelectedWindow = selectedWindow;
            result.Message = "Unable to build a same-speaker or nearby reference clip for the selected match.";
            return result;
        }

        var backgroundTargetPath = Path.Combine(clipsDir, "target.background.wav");
        var referenceVocalPath = Path.Combine(clipsDir, "reference.vocals.wav");
        var useTargetAsReference = Math.Abs(referenceClip.Start - targetClip.Start) < 0.001 && Math.Abs(referenceClip.End - targetClip.End) < 0.001;

        await MediaRenderService.ExtractAudioSegmentAsync(stemArtifacts.VocalStem, targetClip.Path, targetClip.Start, targetClip.End);
        await MediaRenderService.ExtractAudioSegmentAsync(stemArtifacts.BackgroundStem, backgroundTargetPath, targetClip.Start, targetClip.End);
        if (useTargetAsReference)
        {
            File.Copy(targetClip.Path, referenceVocalPath, true);
        }
        else
        {
            await MediaRenderService.ExtractAudioSegmentAsync(stemArtifacts.VocalStem, referenceVocalPath, referenceClip.Start, referenceClip.End);
        }

        result.SelectedMatchIndex = selectedMatchPosition + 1;
        result.SelectedMatch = selectedMatch;
        result.SelectedWindow = selectedWindow;
        result.ReferenceClip = new ReplacePrototypeClip
        {
            Path = referenceVocalPath,
            Start = referenceClip.Start,
            End = referenceClip.End,
            Text = referenceClip.Text,
        };
        result.TargetClip = targetClip;
        result.Artifacts.VocalTargetWindow = targetClip.Path;
        result.Artifacts.BackgroundTargetWindow = backgroundTargetPath;
        result.Artifacts.ReferenceVocalClip = referenceVocalPath;

        var manifestPath = Path.Combine(prototypeDir, "replace-prototype-manifest.json");
        var manifest = new
        {
            input_file = inputPath,
            mode = prototypeConfig.Censor.Mode,
            selected_match_index = result.SelectedMatchIndex,
            selected_match = selectedMatch,
            replacement_text = selectedMatch.ReplacementText,
            vocal_stem = stemArtifacts.VocalStem,
            background_stem = stemArtifacts.BackgroundStem,
            background_clip = backgroundTargetPath,
            target_clip = result.TargetClip,
            reference_clip = result.ReferenceClip,
            subtitle_preview_files = result.SubtitlePreviewFiles,
        };

        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        result.Artifacts.ManifestPath = manifestPath;
        result.Status = "prototype_prepared";
        result.Message = "One-window replace prototype artifacts are ready for OpenVoice synthesis and splice testing.";
        return result;
    }

    private static ReferenceSelection? SelectReferenceClip(
        ProfanityMatch selectedMatch,
        RefinementWindow selectedWindow,
        IReadOnlyList<ProfanityMatch> allMatches,
        ReplacePrototypeClip targetClip)
    {
        var preferredSameSpeakerDuration = targetClip.End - targetClip.Start;
        if (preferredSameSpeakerDuration >= 0.30)
        {
            return new ReferenceSelection
            {
                Start = targetClip.Start,
                End = targetClip.End,
                Text = targetClip.Text ?? string.Empty,
                Distance = 0.0,
            };
        }

        var matchMidpoint = (selectedMatch.Start + selectedMatch.End) / 2.0;

        var candidates = selectedWindow.Segments
            .Where(segment => !SegmentOverlapsAnyMatch(segment, allMatches))
            .Select(segment =>
            {
                var start = Math.Max(0.0, segment.Start - ReferencePaddingSeconds);
                var end = segment.End + ReferencePaddingSeconds;
                if ((end - start) > MaximumReferenceDurationSeconds)
                {
                    end = start + MaximumReferenceDurationSeconds;
                }

                return new ReferenceSelection
                {
                    Start = start,
                    End = Math.Max(start + MinimumReferenceDurationSeconds, end),
                    Text = BuildSegmentText(segment),
                    Distance = DistanceFromWindow(matchMidpoint, segment.Start, segment.End),
                };
            })
            .Where(candidate => (candidate.End - candidate.Start) >= MinimumReferenceDurationSeconds)
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.End - candidate.Start)
            .ToList();

        return candidates.FirstOrDefault();
    }

    private static bool SegmentOverlapsAnyMatch(TranscriptSegment segment, IReadOnlyList<ProfanityMatch> matches)
    {
        return matches.Any(match => segment.Start < match.End && segment.End > match.Start);
    }

    private static double DistanceFromWindow(double midpoint, double start, double end)
    {
        if (midpoint < start)
        {
            return start - midpoint;
        }

        if (midpoint > end)
        {
            return midpoint - end;
        }

        return 0.0;
    }

    private static string BuildSegmentText(TranscriptSegment segment)
    {
        if (segment.Words.Count > 0)
        {
            var joined = string.Join(" ", segment.Words.Select(word => word.Text.Trim())).Trim();
            if (!string.IsNullOrWhiteSpace(joined))
            {
                return joined;
            }
        }

        return segment.Text.Trim();
    }

    private static async Task<StemArtifacts> EnsureDemucsStemsAsync(string inputPath, string prototypeDir, AppConfig config)
    {
        var stemsDir = Path.Combine(prototypeDir, "stems");
        Directory.CreateDirectory(stemsDir);

        var extractedAudioPath = Path.Combine(stemsDir, "full_audio.wav");
        var vocalStemPath = Path.Combine(stemsDir, "vocals.wav");
        var backgroundStemPath = Path.Combine(stemsDir, "background.wav");

        if (File.Exists(vocalStemPath) && File.Exists(backgroundStemPath))
        {
            return new StemArtifacts(extractedAudioPath, vocalStemPath, backgroundStemPath);
        }

        await MediaRenderService.ExtractAudioAsync(inputPath, extractedAudioPath);

        var pythonExecutable = AsrBridgeService.ResolvePythonExecutable("demucs");
        if (pythonExecutable is null)
        {
            throw new InvalidOperationException("No Python executable with `demucs` was found for replace-mode prototyping. Set CENSOR_MEDIA_PYTHON or install demucs in the active runtime.");
        }

        var demucsInputPath = Path.Combine(stemsDir, "demucs_input.wav");
        var demucsOutputDir = Path.Combine(stemsDir, "demucs_output");
        await ToolRunner.RunAsync(
            "ffmpeg",
            "-y",
            "-i",
            extractedAudioPath,
            "-ac",
            "2",
            "-ar",
            "44100",
            demucsInputPath);

        var environmentVariables = AsrBridgeService.BuildBridgeEnvironment();
        var resolvedDevice = await AsrBridgeService.ResolveDemucsDeviceAsync(pythonExecutable, environmentVariables, config);
        var resolvedModel = AsrBridgeService.ResolveDemucsModel(config);
        await ToolRunner.RunCaptureAsync(
            pythonExecutable,
            environmentVariables,
            TimeSpan.FromHours(2),
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
            demucsInputPath);

        var generatedRoot = Path.Combine(demucsOutputDir, resolvedModel, Path.GetFileNameWithoutExtension(demucsInputPath));
        var generatedVocals = Path.Combine(generatedRoot, "vocals.wav");
        var generatedBackground = Path.Combine(generatedRoot, "no_vocals.wav");
        if (!File.Exists(generatedVocals) || !File.Exists(generatedBackground))
        {
            throw new InvalidOperationException("Demucs did not produce both vocals.wav and no_vocals.wav for replace-mode prototyping.");
        }

        File.Move(generatedVocals, vocalStemPath, true);
        File.Move(generatedBackground, backgroundStemPath, true);
        return new StemArtifacts(extractedAudioPath, vocalStemPath, backgroundStemPath);
    }

    private static AppConfig CloneConfig(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config);
        return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
    }

    private sealed record ReferenceSelection
    {
        public double Start { get; init; }
        public double End { get; init; }
        public string Text { get; init; } = string.Empty;
        public double Distance { get; init; }
    }

    private sealed record StemArtifacts(string ExtractedAudio, string VocalStem, string BackgroundStem);
}