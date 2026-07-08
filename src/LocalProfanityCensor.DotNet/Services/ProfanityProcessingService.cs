using LocalProfanityCensor.DotNet.Models;

namespace LocalProfanityCensor.DotNet.Services;

internal static class ProfanityProcessingService
{
    private const double DemucsCleanAudioGainDb = 8.0;

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".m4v",
        ".mov",
        ".mkv",
        ".avi",
        ".mp3",
        ".m4a",
        ".wav",
        ".flac",
    };

    public static async Task<TranscriptResult> TranscribeAsync(string inputPath, AppConfig config)
    {
        ProgressReporter.Report("Inspecting media");
        var mediaInfo = await MediaInspector.InspectAsync(inputPath);
        var workDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? Directory.GetCurrentDirectory(), ".censor_media_transcribe");
        Directory.CreateDirectory(workDir);
        ProgressReporter.Report("Resolving transcript source");
        var transcriptResolution = await TranscriptResolutionService.ResolveAsync(inputPath, mediaInfo, config, workDir);
        var transcript = transcriptResolution.SelectedTranscript;
        if (transcript is null || transcript.Segments.Count == 0)
        {
            var failureMessage = config.Processing.AsrFallback
                ? "No usable transcript source was found. Subtitle resolution failed and full-audio ASR produced no transcript."
                : "No usable transcript source was found. Enable processing.asr_fallback to allow full-audio ASR transcription when subtitle resolution fails.";
            throw new InvalidOperationException(failureMessage);
        }

        ProgressReporter.Report("Transcript resolution completed");
        return transcript;
    }

    public static async Task<ProcessResult> ProcessFileAsync(string inputPath, string outputPath, string dictionaryPath, AppConfig config)
    {
        outputPath = NormalizeOutputPath(inputPath, outputPath);
        var fileName = Path.GetFileName(inputPath);
        ProgressReporter.ReportStage("inspect", "Inspecting media", fileName: fileName, mode: config.Censor.Mode);
        var mediaInfo = await MediaInspector.InspectAsync(inputPath);
        var workDir = BuildWorkDir(outputPath, inputPath);
        Directory.CreateDirectory(workDir);

        var result = new ProcessResult
        {
            InputFile = inputPath,
            OutputFile = outputPath,
            Status = "failed",
            DurationSeconds = mediaInfo.DurationSeconds,
            WorkDir = workDir,
        };

        if (mediaInfo.AudioStreams.Count == 0)
        {
            result.FailureReason = "No audio stream found";
            return result;
        }

        if (File.Exists(outputPath) && !config.Processing.Overwrite)
        {
            result.Status = "skipped";
            result.FailureReason = "Output file exists and overwrite is false";
            return result;
        }

        ProgressReporter.ReportStage("transcript", "Resolving transcript source", fileName: fileName, mode: config.Censor.Mode);
        var transcriptResolution = await TranscriptResolutionService.ResolveAsync(inputPath, mediaInfo, config, workDir);
        var transcript = transcriptResolution.SelectedTranscript;
        if (transcript is null || transcript.Segments.Count == 0)
        {
            result.FailureReason = "No usable transcript source. Subtitle resolution failed and full-audio ASR fallback is disabled or produced no transcript.";
            result.Warnings.AddRange(transcriptResolution.Warnings);
            await ReportService.WriteArtifactsAsync(workDir, mediaInfo, transcriptResolution, result);
            await ReportService.WriteReportsAsync(Path.ChangeExtension(outputPath, null) ?? outputPath, result, mediaInfo, transcriptResolution, config);
            return result;
        }

        result.TextSource = transcript.Source;
        result.Warnings.AddRange(transcriptResolution.Warnings);

        var analysisTranscript = transcript;
        var alignmentRequest = AlignmentRequestService.Build(inputPath, transcriptResolution);
        if (alignmentRequest is not null)
        {
            ProgressReporter.ReportStage("alignment", "Running transcript alignment prototype", fileName: fileName, mode: config.Censor.Mode);
            var alignedTranscript = AlignmentPrototypeService.Align(alignmentRequest);
            var improvedTimingWordCount = alignedTranscript.Summary.AdoptedTimingWordCount
                + alignedTranscript.Summary.PhraseAlignedWordCount
                + alignedTranscript.Summary.InterpolatedTimingWordCount;
            if (improvedTimingWordCount > 0)
            {
                analysisTranscript = TranscriptArtifactService.ToTranscriptResult(alignedTranscript.Transcript);
                result.TextSource = analysisTranscript.Source;
                result.Warnings.AddRange(alignedTranscript.Warnings);
                result.Warnings.Add($"Using prototype aligned transcript timings from {alignmentRequest.SelectedKind} with reference {alignmentRequest.ReferenceKind ?? "none"}.");
            }
        }

        ProgressReporter.ReportStage("match", "Detecting profanity matches", fileName: fileName, mode: config.Censor.Mode);
        var dictionary = DictionaryService.LoadPreparedDictionary(dictionaryPath);
        var coarseMatches = ProfanityMatcher.DetectProfanity(analysisTranscript.Segments, dictionary);
        ProgressReporter.ReportStage("refine", "Refining profanity timings", fileName: fileName, mode: config.Censor.Mode, detail: $"coarse matches {coarseMatches.Count}");
        var refinement = await RefinementService.RefineAsync(inputPath, analysisTranscript, coarseMatches, dictionary, config, workDir);
        result.Refinement = refinement;
        result.Matches = ApplyConfiguredCensorMode(refinement.Matches, config);
        result.Warnings.AddRange(refinement.Warnings);
        result.Ranges = CensorRangeBuilder.BuildCensorRanges(
            result.Matches,
            mediaInfo.DurationSeconds,
            config.Censor.PaddingStartMs,
            config.Censor.PaddingEndMs,
            config.Censor.MergeGapMs);

        var generatedSubtitleTracks = new List<GeneratedSubtitleTrack>();
        if (mediaInfo.VideoStreams.Count > 0)
        {
            generatedSubtitleTracks = await GeneratedSubtitleService.CreateTracksAsync(
                analysisTranscript,
                result.Matches,
                mediaInfo,
                config,
                workDir,
                includeCensoredTrack: result.Ranges.Count > 0);
            result.GeneratedSubtitleFiles.AddRange(generatedSubtitleTracks.Select(track => track.Path));
        }

        ProgressReporter.ReportStage("artifacts", "Writing analysis artifacts", fileName: fileName, mode: config.Censor.Mode, detail: $"matches {result.Matches.Count}");
        await ReportService.WriteArtifactsAsync(workDir, mediaInfo, transcriptResolution, result);

        if (config.DryRun)
        {
            result.Status = "dry_run_completed";
            result.Message = "Transcript analysis completed; audio render/remux is skipped because --dry-run was supplied.";
            ProgressReporter.ReportStage("reports", "Writing dry-run reports", fileName: fileName, mode: config.Censor.Mode);
            await ReportService.WriteReportsAsync(Path.ChangeExtension(outputPath, null) ?? outputPath, result, mediaInfo, transcriptResolution, config);
            CleanupWorkDir(workDir, config);
            ProgressReporter.ReportStage("complete", "Dry run complete", fileName: fileName, mode: config.Censor.Mode);
            return result;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? Directory.GetCurrentDirectory());
        if (result.Ranges.Count == 0)
        {
            if (generatedSubtitleTracks.Count > 0)
            {
                await MediaRenderService.RemuxOutputAsync(inputPath, null, outputPath, mediaInfo, generatedSubtitleTracks, config);
                result.Status = "completed_subtitle_only";
                result.Message = "No censor ranges were detected; media was remuxed only to add generated subtitle tracks.";
            }
            else
            {
                result.OutputFile = null;
                result.Status = "completed_no_changes";
                result.Message = "No censor ranges were detected and no subtitle track needed to be generated; no output media was written.";
            }

            await ReportService.WriteReportsAsync(Path.ChangeExtension(outputPath, null) ?? outputPath, result, mediaInfo, transcriptResolution, config);
            CleanupWorkDir(workDir, config);
            return result;
        }

        var inputAudio = Path.Combine(workDir, "input.wav");
        var mutedAudio = Path.Combine(workDir, "muted.wav");
        var beepAudio = Path.Combine(workDir, "beeps.wav");
        var censoredAudio = Path.Combine(workDir, "censored.wav");
        var cleanAudioOutput = mediaInfo.VideoStreams.Count == 0 ? outputPath : Path.ChangeExtension(outputPath, ".flac");

        if (string.Equals(config.Censor.Mode, "replace", StringComparison.OrdinalIgnoreCase))
        {
            ProgressReporter.ReportStage("render", "Rendering replace-mode audio", fileName: fileName, mode: config.Censor.Mode, detail: $"matches {result.Matches.Count}");
            await ReplaceWorkflowService.RenderAsync(inputPath, mediaInfo, result, cleanAudioOutput, censoredAudio, generatedSubtitleTracks, outputPath, config, workDir);
            result.Status = result.Warnings.Count > 0 ? "completed_with_warnings" : "completed";
            ProgressReporter.ReportStage("reports", "Writing final reports", fileName: fileName, mode: config.Censor.Mode);
            await ReportService.WriteReportsAsync(Path.ChangeExtension(outputPath, null) ?? outputPath, result, mediaInfo, transcriptResolution, config);
            CleanupWorkDir(workDir, config);
            ProgressReporter.ReportStage("complete", "Processing complete", fileName: fileName, mode: config.Censor.Mode);
            return result;
        }

        if (string.Equals(config.Transcription.DialogIsolation, "demucs", StringComparison.OrdinalIgnoreCase))
        {
            ProgressReporter.ReportStage("stems", "Preparing Demucs stems", fileName: fileName, mode: config.Censor.Mode);
            var stemArtifacts = await EnsureNormalModeStemsAsync(inputPath, workDir, config);
            var remixedAudio = Path.Combine(workDir, "censored.remix.wav");

            ProgressReporter.ReportStage("render", "Rendering muted vocal stem", fileName: fileName, mode: config.Censor.Mode);
            await MediaRenderService.RenderMutedAudioFromStemAsync(stemArtifacts.VocalStem, mutedAudio, result.Ranges, config);
            ProgressReporter.ReportStage("render", "Rendering beep track for vocal stem", fileName: fileName, mode: config.Censor.Mode);
            await MediaRenderService.RenderBeepTrackForStemAsync(beepAudio, mediaInfo.DurationSeconds, result.Ranges, config, stemArtifacts.VocalStem);
            ProgressReporter.ReportStage("mix", "Mixing censored vocal stem", fileName: fileName, mode: config.Censor.Mode);
            await MediaRenderService.MixAudioTracksAsync(mutedAudio, beepAudio, censoredAudio);
            ProgressReporter.ReportStage("mix", "Remixing vocals with background stem", fileName: fileName, mode: config.Censor.Mode);
            await MediaRenderService.MixAudioTracksAsync(censoredAudio, stemArtifacts.BackgroundStem, remixedAudio);
            ProgressReporter.ReportStage("encode", "Encoding output audio", fileName: fileName, mode: config.Censor.Mode);
            await MediaRenderService.EncodeCleanAudioAsync(remixedAudio, cleanAudioOutput, mediaInfo, DemucsCleanAudioGainDb);
            result.CleanAudioFile = cleanAudioOutput;
            ProgressReporter.ReportStage("remux", "Remuxing output media", fileName: fileName, mode: config.Censor.Mode);
            await MediaRenderService.RemuxOutputAsync(inputPath, cleanAudioOutput, outputPath, mediaInfo, generatedSubtitleTracks, config);

            result.Status = result.Warnings.Count > 0 ? "completed_with_warnings" : "completed";
            result.Message = "Processing completed.";
            ProgressReporter.ReportStage("reports", "Writing final reports", fileName: fileName, mode: config.Censor.Mode);
            await ReportService.WriteReportsAsync(Path.ChangeExtension(outputPath, null) ?? outputPath, result, mediaInfo, transcriptResolution, config);
            CleanupWorkDir(workDir, config);
            ProgressReporter.ReportStage("complete", "Processing complete", fileName: fileName, mode: config.Censor.Mode);
            return result;
        }

        ProgressReporter.ReportStage("extract", "Extracting source audio", fileName: fileName, mode: config.Censor.Mode);
        await MediaRenderService.ExtractAudioAsync(inputPath, inputAudio);
        ProgressReporter.ReportStage("render", "Rendering muted audio", fileName: fileName, mode: config.Censor.Mode);
        await MediaRenderService.RenderMutedAudioAsync(inputAudio, mutedAudio, result.Ranges, config);
        ProgressReporter.ReportStage("render", "Rendering beep track", fileName: fileName, mode: config.Censor.Mode);
        await MediaRenderService.RenderBeepTrackAsync(beepAudio, mediaInfo.DurationSeconds, result.Ranges, config, mediaInfo);
        ProgressReporter.ReportStage("mix", "Mixing censored audio", fileName: fileName, mode: config.Censor.Mode);
        await MediaRenderService.MixAudioTracksAsync(mutedAudio, beepAudio, censoredAudio);
        ProgressReporter.ReportStage("encode", "Encoding output audio", fileName: fileName, mode: config.Censor.Mode);
        await MediaRenderService.EncodeCleanAudioAsync(censoredAudio, cleanAudioOutput, mediaInfo);
        result.CleanAudioFile = cleanAudioOutput;
        ProgressReporter.ReportStage("remux", "Remuxing output media", fileName: fileName, mode: config.Censor.Mode);
        await MediaRenderService.RemuxOutputAsync(inputPath, cleanAudioOutput, outputPath, mediaInfo, generatedSubtitleTracks, config);

        result.Status = result.Warnings.Count > 0 ? "completed_with_warnings" : "completed";
        result.Message = "Processing completed.";
        ProgressReporter.ReportStage("reports", "Writing final reports", fileName: fileName, mode: config.Censor.Mode);
        await ReportService.WriteReportsAsync(Path.ChangeExtension(outputPath, null) ?? outputPath, result, mediaInfo, transcriptResolution, config);
        CleanupWorkDir(workDir, config);
        ProgressReporter.ReportStage("complete", "Processing complete", fileName: fileName, mode: config.Censor.Mode);
        return result;
    }

    public static async Task<List<ProcessResult>> ProcessFolderAsync(string inputDir, string outputDir, string dictionaryPath, AppConfig config)
    {
        var results = new List<ProcessResult>();
        foreach (var filePath in Directory.EnumerateFiles(inputDir, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!MediaExtensions.Contains(Path.GetExtension(filePath)))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(inputDir, filePath);
            var outputExtension = ShouldNormalizeVideoOutputToMkv(filePath)
                ? ".mkv"
                : Path.GetExtension(relativePath);
            var outputName = $"{Path.GetFileNameWithoutExtension(relativePath)}.clean{outputExtension}";
            var outputPath = Path.Combine(outputDir, Path.GetDirectoryName(relativePath) ?? string.Empty, outputName);

            try
            {
                ProgressReporter.ReportStage("process-file", "Processing file", fileName: relativePath, mode: config.Censor.Mode);
                results.Add(await ProcessFileAsync(filePath, outputPath, dictionaryPath, config));
            }
            catch (Exception ex)
            {
                results.Add(new ProcessResult
                {
                    InputFile = filePath,
                    OutputFile = outputPath,
                    Status = "failed",
                    FailureReason = ex.Message,
                });
            }
        }

        return results;
    }

    private static string BuildWorkDir(string outputPath, string inputPath)
    {
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? Directory.GetCurrentDirectory();
        var safeStem = SlugifyFileStem(Path.GetFileNameWithoutExtension(inputPath));
        return Path.Combine(outputDirectory, ".work", safeStem);
    }

    private static string NormalizeOutputPath(string inputPath, string outputPath)
    {
        if (!ShouldNormalizeVideoOutputToMkv(inputPath))
        {
            return outputPath;
        }

        return Path.ChangeExtension(outputPath, ".mkv") ?? outputPath;
    }

    private static bool ShouldNormalizeVideoOutputToMkv(string inputPath)
    {
        var extension = Path.GetExtension(inputPath);
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".avi", StringComparison.OrdinalIgnoreCase);
    }

    private static List<ProfanityMatch> ApplyConfiguredCensorMode(IReadOnlyList<ProfanityMatch> matches, AppConfig config)
    {
        var effectiveMode = string.IsNullOrWhiteSpace(config.Censor.Mode)
            ? "mute"
            : config.Censor.Mode;

        return matches.Select(match => new ProfanityMatch
        {
            TermId = match.TermId,
            MatchedText = match.MatchedText,
            NormalizedText = match.NormalizedText,
            Start = match.Start,
            End = match.End,
            Source = match.Source,
            Confidence = match.Confidence,
            Severity = match.Severity,
            Action = effectiveMode,
            ReplacementText = match.ReplacementText,
            ContextBefore = match.ContextBefore,
            ContextAfter = match.ContextAfter,
        }).ToList();
    }

    private static string SlugifyFileStem(string value)
    {
        var builder = new List<char>(value.Length);
        var previousWasDash = false;
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Add(char.ToLowerInvariant(character));
                previousWasDash = false;
            }
            else if (!previousWasDash)
            {
                builder.Add('-');
                previousWasDash = true;
            }
        }

        return new string(builder.ToArray()).Trim('-');
    }


    private static async Task<(string ExtractedAudio, string VocalStem, string BackgroundStem)> EnsureNormalModeStemsAsync(
        string inputPath,
        string workDir,
        AppConfig config)
    {
        var extractedAudioPath = Path.Combine(workDir, "input.wav");
        var vocalStemPath = Path.Combine(workDir, "full_audio.demucs.vocals.wav");
        var backgroundStemPath = Path.Combine(workDir, "full_audio.demucs.no_vocals.wav");

        if (File.Exists(extractedAudioPath) && File.Exists(vocalStemPath) && File.Exists(backgroundStemPath))
        {
            return (extractedAudioPath, vocalStemPath, backgroundStemPath);
        }

        var cachedBackgroundPath = Directory.Exists(Path.Combine(workDir, "demucs"))
            ? Directory.EnumerateFiles(Path.Combine(workDir, "demucs"), "no_vocals.wav", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (File.Exists(extractedAudioPath) && File.Exists(vocalStemPath) && !string.IsNullOrWhiteSpace(cachedBackgroundPath) && File.Exists(cachedBackgroundPath))
        {
            File.Copy(cachedBackgroundPath, backgroundStemPath, overwrite: true);
            return (extractedAudioPath, vocalStemPath, backgroundStemPath);
        }

        var normalRoot = Path.Combine(workDir, "normal_mode_stems");
        var generated = await ReplaceWorkflowService.EnsureStemsAsync(inputPath, workDir, normalRoot, config);
        File.Copy(generated.ExtractedAudio, extractedAudioPath, overwrite: true);
        File.Copy(generated.VocalStem, vocalStemPath, overwrite: true);
        File.Copy(generated.BackgroundStem, backgroundStemPath, overwrite: true);
        return (extractedAudioPath, vocalStemPath, backgroundStemPath);
    }

    private static void CleanupWorkDir(string workDir, AppConfig config)
    {
        if (config.KeepWork)
        {
            return;
        }

        foreach (var fileName in new[] { "input.wav", "muted.wav", "beeps.wav", "censored.wav" })
        {
            var filePath = Path.Combine(workDir, fileName);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        if (Directory.Exists(workDir) && !Directory.EnumerateFileSystemEntries(workDir).Any())
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}