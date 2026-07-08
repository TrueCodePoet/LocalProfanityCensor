using System.Text.Json;
using LocalProfanityCensor.DotNet.Models;

namespace LocalProfanityCensor.DotNet.Services;

internal static class ReplaceWorkflowService
{
    private const double ReplaceTargetLeadPaddingSeconds = 0.20;
    private const double ReplaceTargetTrailPaddingSeconds = 0.35;
    private const double ReplaceReferencePaddingSeconds = 0.08;
    private const double ReplaceMinimumReferenceDurationSeconds = 0.80;
    private const double ReplaceMaximumReferenceDurationSeconds = 6.00;
    private const double ReplaceMaximumSentenceDurationSeconds = 5.00;

    public static async Task RenderAsync(
        string inputPath,
        MediaInfo mediaInfo,
        ProcessResult result,
        string cleanAudioOutput,
        string censoredAudio,
        IReadOnlyList<GeneratedSubtitleTrack> generatedSubtitleTracks,
        string outputPath,
        AppConfig config,
        string workDir)
    {
        var replaceableMatches = result.Matches
            .Where(match => !string.IsNullOrWhiteSpace(match.ReplacementText))
            .OrderBy(match => match.Start)
            .ThenBy(match => match.End)
            .ToList();

        if (replaceableMatches.Count == 0)
        {
            throw new InvalidOperationException("Replace mode requires at least one profanity match with dictionary replacement text.");
        }

        var skippedMatches = result.Matches.Count - replaceableMatches.Count;
        if (skippedMatches > 0)
        {
            result.Warnings.Add($"Skipped {skippedMatches} profanity match(es) in replace mode because no replacement text was available.");
        }

        var replaceEngine = ResolveReplaceEngine(config);
        var replaceRoot = Path.Combine(workDir, $"{replaceEngine}_replace");
        Directory.CreateDirectory(replaceRoot);
        var fileName = Path.GetFileName(inputPath);

        ProgressReporter.ReportStage("stems", "Preparing replace stems", fileName: fileName, mode: "replace", current: 0, total: replaceableMatches.Count);
        var stemArtifacts = await EnsureStemsAsync(inputPath, workDir, replaceRoot, config);
        var currentVocalStemPath = Path.Combine(replaceRoot, "vocals.current.wav");
        File.Copy(stemArtifacts.VocalStem, currentVocalStemPath, overwrite: true);

        for (var index = 0; index < replaceableMatches.Count; index++)
        {
            var match = replaceableMatches[index];
            var refinementWindow = result.Refinement?.Windows
                .FirstOrDefault(window => window.Start <= match.Start && window.End >= match.End);
            if (refinementWindow is null)
            {
                throw new InvalidOperationException($"Replace mode could not locate a refinement window for match {index + 1}: {match.MatchedText.Trim()} at {match.Start:F2}s.");
            }

            var targetClip = BuildSentenceTargetClip(match, refinementWindow);
            var referenceClip = SelectSentenceReferenceClip(refinementWindow, targetClip, replaceableMatches);
            if (referenceClip is null)
            {
                throw new InvalidOperationException($"Replace mode could not build a reference clip for match {index + 1}: {match.MatchedText.Trim()} at {match.Start:F2}s.");
            }

            var replacementPhraseText = BuildReplacementPhraseText(match, refinementWindow);

            var matchDir = Path.Combine(replaceRoot, $"match-{index + 1:D4}");
            var clipsDir = Path.Combine(matchDir, "clips");
            Directory.CreateDirectory(clipsDir);

            var percent = replaceableMatches.Count == 0 ? 100.0 : ((index + 1) * 100.0) / replaceableMatches.Count;
            ProgressReporter.ReportStage(
                "replace",
                $"Replacing '{match.ReplacementText}'",
                fileName: fileName,
                mode: "replace",
                current: index + 1,
                total: replaceableMatches.Count,
                percent: percent,
                mediaTimeSeconds: match.Start,
                detail: match.MatchedText.Trim());

            var targetClipPath = Path.Combine(clipsDir, "target.vocals.wav");
            await MediaRenderService.ExtractAudioSegmentAsync(currentVocalStemPath, targetClipPath, targetClip.Start, targetClip.End);

            var referenceClipPath = Path.Combine(clipsDir, "reference.vocals.wav");
            await MediaRenderService.ExtractAudioSegmentAsync(currentVocalStemPath, referenceClipPath, referenceClip.Start, referenceClip.End);

            var manifestPath = Path.Combine(matchDir, "replace-prototype-manifest.json");
            var manifest = new
            {
                input_file = inputPath,
                mode = "replace",
                selected_match_index = index + 1,
                selected_match = match,
                replacement_text = match.ReplacementText,
                replacement_phrase_text = replacementPhraseText,
                vocal_stem = currentVocalStemPath,
                background_stem = stemArtifacts.BackgroundStem,
                background_clip = stemArtifacts.BackgroundStem,
                target_clip = new ReplacePrototypeClip
                {
                    Path = targetClipPath,
                    Start = 0.0,
                    End = targetClip.End - targetClip.Start,
                    Text = targetClip.Text,
                    MatchStart = targetClip.MatchStart,
                    MatchEnd = targetClip.MatchEnd,
                },
                reference_clip = new ReplacePrototypeClip
                {
                    Path = referenceClipPath,
                    Start = referenceClip.Start,
                    End = referenceClip.End,
                    Text = referenceClip.Text,
                    MatchStart = referenceClip.MatchStart,
                    MatchEnd = referenceClip.MatchEnd,
                },
            };

            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            var synthResult = await RunReplaceSynthesisAsync(replaceEngine, manifestPath, matchDir);

            result.Warnings.AddRange(synthResult.Warnings.Select(warning => $"replace match {index + 1}: {warning}"));
            if (!string.Equals(synthResult.Status, "completed", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(synthResult.ReplacedVocalClip)
                || !File.Exists(synthResult.ReplacedVocalClip))
            {
                throw new InvalidOperationException($"Replace mode synthesis failed for match {index + 1}: {synthResult.Message ?? "No replacement audio was produced."}");
            }

            var updatedVocalStemPath = Path.Combine(matchDir, "vocals.updated.wav");
            await SpliceClipIntoStemAsync(
                currentVocalStemPath,
                synthResult.ReplacedVocalClip,
                updatedVocalStemPath,
                targetClip.Start,
                targetClip.End);

            File.Copy(updatedVocalStemPath, currentVocalStemPath, overwrite: true);
        }

        await MediaRenderService.MixAudioTracksAsync(currentVocalStemPath, stemArtifacts.BackgroundStem, censoredAudio);
        ProgressReporter.ReportStage("encode", "Encoding output audio", fileName: fileName, mode: "replace", current: replaceableMatches.Count, total: replaceableMatches.Count, percent: 100.0);
        await MediaRenderService.EncodeCleanAudioAsync(censoredAudio, cleanAudioOutput, mediaInfo);
        result.CleanAudioFile = cleanAudioOutput;
        ProgressReporter.ReportStage("remux", "Remuxing output media", fileName: fileName, mode: "replace", current: replaceableMatches.Count, total: replaceableMatches.Count, percent: 100.0);
        await MediaRenderService.RemuxOutputAsync(inputPath, cleanAudioOutput, outputPath, mediaInfo, generatedSubtitleTracks, config);
        result.Message = $"Processing completed with {replaceEngine} replacements for {replaceableMatches.Count} match(es).";
    }

    public static async Task<(string ExtractedAudio, string VocalStem, string BackgroundStem)> EnsureStemsAsync(
        string inputPath,
        string workDir,
        string replaceRoot,
        AppConfig config)
    {
        var stemsDir = Path.Combine(replaceRoot, "stems");
        Directory.CreateDirectory(stemsDir);

        var extractedAudioPath = Path.Combine(stemsDir, "full_audio.wav");
        var vocalStemPath = Path.Combine(stemsDir, "vocals.wav");
        var backgroundStemPath = Path.Combine(stemsDir, "background.wav");
        if (File.Exists(vocalStemPath) && File.Exists(backgroundStemPath))
        {
            return (extractedAudioPath, vocalStemPath, backgroundStemPath);
        }

        var cachedVocalsPath = Path.Combine(workDir, "full_audio.demucs.vocals.wav");
        var cachedBackgroundPath = Directory.Exists(Path.Combine(workDir, "demucs"))
            ? Directory.EnumerateFiles(Path.Combine(workDir, "demucs"), "no_vocals.wav", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (File.Exists(cachedVocalsPath) && !string.IsNullOrWhiteSpace(cachedBackgroundPath) && File.Exists(cachedBackgroundPath))
        {
            var cachedExtractedAudioPath = Path.Combine(workDir, "full_audio.wav");
            if (File.Exists(cachedExtractedAudioPath))
            {
                File.Copy(cachedExtractedAudioPath, extractedAudioPath, overwrite: true);
            }

            File.Copy(cachedVocalsPath, vocalStemPath, overwrite: true);
            File.Copy(cachedBackgroundPath, backgroundStemPath, overwrite: true);
            return (extractedAudioPath, vocalStemPath, backgroundStemPath);
        }

        await MediaRenderService.ExtractAudioAsync(inputPath, extractedAudioPath);

        var pythonExecutable = AsrBridgeService.ResolvePythonExecutable("demucs");
        if (pythonExecutable is null)
        {
            throw new InvalidOperationException("No Python executable with `demucs` was found for replace mode. Set CENSOR_MEDIA_PYTHON or install demucs in the active runtime.");
        }

        var demucsInputPath = Path.Combine(stemsDir, "demucs_input.wav");
        var demucsOutputDir = Path.Combine(stemsDir, "demucs_output");
        await ToolRunner.RunAsync(
            "ffmpeg",
            new ToolRunner.CommandProgressInfo("stems", "Preparing replace-mode Demucs input", null, Path.GetFileName(extractedAudioPath), "replace"),
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
            new ToolRunner.CommandProgressInfo("stems", "Running Demucs for replace mode", $"model {resolvedModel} on {resolvedDevice}", Path.GetFileName(demucsInputPath), "replace"),
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
            throw new InvalidOperationException("Demucs did not produce both vocals.wav and no_vocals.wav for replace mode.");
        }

        File.Move(generatedVocals, vocalStemPath, true);
        File.Move(generatedBackground, backgroundStemPath, true);
        return (extractedAudioPath, vocalStemPath, backgroundStemPath);
    }

    private static ReplacePrototypeClip BuildSentenceTargetClip(ProfanityMatch match, RefinementWindow selectedWindow)
    {
        var containingSegment = selectedWindow.Segments
            .Where(segment => segment.Start <= match.End && segment.End >= match.Start)
            .OrderBy(segment => Math.Abs(((segment.Start + segment.End) / 2.0) - ((match.Start + match.End) / 2.0)))
            .FirstOrDefault();

        if (containingSegment is not null)
        {
            var segmentStart = Math.Max(0.0, containingSegment.Start - ReplaceTargetLeadPaddingSeconds);
            var segmentEnd = containingSegment.End + ReplaceTargetTrailPaddingSeconds;
            if ((segmentEnd - segmentStart) <= ReplaceMaximumSentenceDurationSeconds)
            {
                return new ReplacePrototypeClip
                {
                    Start = segmentStart,
                    End = segmentEnd,
                    Text = BuildSegmentText(containingSegment),
                    MatchStart = Math.Max(0.0, match.Start - segmentStart),
                    MatchEnd = Math.Max(0.0, match.End - segmentStart),
                };
            }
        }

        return new ReplacePrototypeClip
        {
            Start = Math.Max(0.0, match.Start - ReplaceTargetLeadPaddingSeconds),
            End = Math.Max(match.End + ReplaceTargetTrailPaddingSeconds, match.Start + 0.30),
            Text = match.MatchedText,
            MatchStart = ReplaceTargetLeadPaddingSeconds,
            MatchEnd = ReplaceTargetLeadPaddingSeconds + Math.Max(0.05, match.End - match.Start),
        };
    }

    private static ReplacePrototypeClip? SelectSentenceReferenceClip(
        RefinementWindow selectedWindow,
        ReplacePrototypeClip targetClip,
        IReadOnlyList<ProfanityMatch> allReplaceableMatches)
    {
        var preferredSameSpeakerDuration = targetClip.End - targetClip.Start;
        if (preferredSameSpeakerDuration >= 0.30)
        {
            return new ReplacePrototypeClip
            {
                Start = targetClip.Start,
                End = targetClip.End,
                Text = targetClip.Text,
                MatchStart = targetClip.MatchStart,
                MatchEnd = targetClip.MatchEnd,
            };
        }

        var matchMidpoint = targetClip.Start + ((targetClip.MatchStart ?? 0.0) + (targetClip.MatchEnd ?? 0.0)) / 2.0;

        return selectedWindow.Segments
            .Where(segment => !SegmentOverlapsMatch(segment, allReplaceableMatches))
            .Select(segment =>
            {
                var start = Math.Max(0.0, segment.Start - ReplaceReferencePaddingSeconds);
                var end = segment.End + ReplaceReferencePaddingSeconds;
                if ((end - start) > ReplaceMaximumReferenceDurationSeconds)
                {
                    end = start + ReplaceMaximumReferenceDurationSeconds;
                }

                return new
                {
                    Clip = new ReplacePrototypeClip
                    {
                        Start = start,
                        End = Math.Max(start + ReplaceMinimumReferenceDurationSeconds, end),
                        Text = BuildSegmentText(segment),
                    },
                    Distance = DistanceFromWindow(matchMidpoint, segment.Start, segment.End),
                };
            })
            .Where(candidate => (candidate.Clip.End - candidate.Clip.Start) >= ReplaceMinimumReferenceDurationSeconds)
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Clip.End - candidate.Clip.Start)
            .Select(candidate => candidate.Clip)
            .FirstOrDefault();
    }

    private static bool SegmentOverlapsMatch(TranscriptSegment segment, IReadOnlyList<ProfanityMatch> matches)
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

    private static string BuildReplacementPhraseText(ProfanityMatch selectedMatch, RefinementWindow selectedWindow)
    {
        var containingSegment = selectedWindow.Segments
            .Where(segment => segment.Start <= selectedMatch.End && segment.End >= selectedMatch.Start)
            .OrderBy(segment => Math.Abs(((segment.Start + segment.End) / 2.0) - ((selectedMatch.Start + selectedMatch.End) / 2.0)))
            .FirstOrDefault();

        var originalText = BuildSegmentText(containingSegment ?? new TranscriptSegment { Text = $"{selectedMatch.ContextBefore} {selectedMatch.MatchedText} {selectedMatch.ContextAfter}" }).Trim();
        if (string.IsNullOrWhiteSpace(originalText))
        {
            originalText = string.Join(" ", new[] { selectedMatch.ContextBefore, selectedMatch.MatchedText, selectedMatch.ContextAfter }
                .Where(value => !string.IsNullOrWhiteSpace(value)))
                .Trim();
        }

        if (string.IsNullOrWhiteSpace(originalText))
        {
            return selectedMatch.ReplacementText ?? string.Empty;
        }

        var matchedText = selectedMatch.MatchedText?.Trim();
        var replacementText = selectedMatch.ReplacementText?.Trim();
        if (string.IsNullOrWhiteSpace(matchedText) || string.IsNullOrWhiteSpace(replacementText))
        {
            return originalText;
        }

        var matchedWord = NormalizeTokenForReplacement(matchedText);
        var replaced = ReplaceMatchedToken(originalText, matchedText, matchedWord, replacementText);

        return string.IsNullOrWhiteSpace(replaced) ? originalText : replaced.Trim();
    }

    private static string ReplaceMatchedToken(string originalText, string matchedText, string matchedWord, string replacementText)
    {
        if (!string.IsNullOrWhiteSpace(matchedWord))
        {
            var wordPattern = $@"(?<!\p{{L}}){System.Text.RegularExpressions.Regex.Escape(matchedWord)}(?<suffix>[^\s\p{{L}}]*)";
            var wordReplaced = System.Text.RegularExpressions.Regex.Replace(
                originalText,
                wordPattern,
                match => replacementText + (match.Groups["suffix"].Success ? match.Groups["suffix"].Value : string.Empty),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1));

            if (!string.Equals(wordReplaced, originalText, StringComparison.Ordinal))
            {
                return wordReplaced;
            }
        }

        return System.Text.RegularExpressions.Regex.Replace(
            originalText,
            System.Text.RegularExpressions.Regex.Escape(matchedText),
            replacementText,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
    }

    private static string NormalizeTokenForReplacement(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = new string(value.Trim().Where(character => char.IsLetter(character) || character == '\'' || character == '-').ToArray());
        return normalized.Trim('\'', '-');
    }

    private static async Task SpliceClipIntoStemAsync(
        string sourceStemPath,
        string replacementClipPath,
        string outputPath,
        double clipStart,
        double clipEnd)
    {
        var clipStartSeconds = clipStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        var clipEndSeconds = clipEnd.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        var filter = string.Join(';', new[]
        {
            $"[0:a]atrim=0:{clipStartSeconds},asetpts=N/SR/TB[pre]",
            $"[0:a]atrim=start={clipEndSeconds},asetpts=N/SR/TB[post]",
            "[1:a]asetpts=N/SR/TB[mid]",
            "[pre][mid][post]concat=n=3:v=0:a=1[aout]",
        });

        await ToolRunner.RunAsync(
            "ffmpeg",
            new ToolRunner.CommandProgressInfo("replace", "Splicing replacement into full vocal stem", null, Path.GetFileName(sourceStemPath), "replace"),
            "-y",
            "-i",
            sourceStemPath,
            "-i",
            replacementClipPath,
            "-filter_complex",
            filter,
            "-map",
            "[aout]",
            outputPath);
    }

    private static string ResolveReplaceEngine(AppConfig config)
    {
        var engine = config.Censor.ReplaceEngine?.Trim();
        if (string.IsNullOrWhiteSpace(engine))
        {
            return "openvoice";
        }

        return engine.ToLowerInvariant();
    }

    private static Task<ReplaceSynthesisResult> RunReplaceSynthesisAsync(string replaceEngine, string manifestPath, string matchDir)
    {
        return replaceEngine switch
        {
            "cosyvoice" => CosyVoicePrototypeService.RunAsync(manifestPath, Path.Combine(matchDir, "cosyvoice-preview"), "auto"),
            _ => OpenVoicePrototypeService.RunAsync(manifestPath, Path.Combine(matchDir, "openvoice-preview"), "auto", null, "en-default", "EN"),
        };
    }
}