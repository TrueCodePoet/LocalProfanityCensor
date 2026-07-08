using LocalProfanityCensor.DotNet.Models;

namespace LocalProfanityCensor.DotNet.Services;

internal static class TranscriptResolutionService
{
    public static async Task<TranscriptResolutionResult> ResolveAsync(string inputPath, MediaInfo mediaInfo, AppConfig config, string workDir)
    {
        var result = new TranscriptResolutionResult();

        ProgressReporter.Report("Checking subtitle transcript candidates");
        var subtitleTranscript = await SubtitleService.ResolveTranscriptAsync(inputPath, mediaInfo, config, workDir);
        if (subtitleTranscript is not null)
        {
            var subtitleArtifact = TranscriptArtifactService.Build(inputPath, subtitleTranscript);
            result.SelectedSource = subtitleTranscript.Source;
            result.SelectionReason = "Accepted subtitle transcript met subtitle selection thresholds.";
            result.SelectedTranscript = subtitleTranscript;
            result.Attempts.Add(new TranscriptResolutionAttempt
            {
                Kind = "subtitle",
                Status = "selected",
                Reason = "Subtitle transcript passed selection.",
                Transcript = subtitleArtifact,
            });

            if (config.Processing.CollectReferenceTranscripts)
            {
                ProgressReporter.Report("Collecting reference full-audio ASR transcript");
                await AddReferenceAsrAttemptAsync(result, inputPath, mediaInfo, config, workDir, subtitleArtifact);
            }
            else
            {
                result.Attempts.Add(new TranscriptResolutionAttempt
                {
                    Kind = "full_audio_asr",
                    Status = config.Processing.AsrFallback ? "not_attempted" : "disabled",
                    Reason = config.Processing.AsrFallback
                        ? "Full-audio ASR was skipped because subtitle resolution already succeeded and processing.collect_reference_transcripts is false."
                        : "Full-audio ASR fallback is disabled in processing.asr_fallback.",
                });
            }

            result.Warnings.AddRange(subtitleTranscript.Warnings);
            return result;
        }

        result.Attempts.Add(new TranscriptResolutionAttempt
        {
            Kind = "subtitle",
            Status = "unavailable",
            Reason = "No acceptable subtitle transcript was found.",
        });

        if (!config.Processing.AsrFallback)
        {
            result.Attempts.Add(new TranscriptResolutionAttempt
            {
                Kind = "full_audio_asr",
                Status = "disabled",
                Reason = "Full-audio ASR fallback is disabled in processing.asr_fallback.",
            });
            result.SelectionReason = "Subtitle resolution failed and ASR fallback is disabled.";
            return result;
        }

        ProgressReporter.Report("Falling back to full-audio ASR transcript");
        var asrTranscript = await AsrBridgeService.TranscribeFullAudioAsync(inputPath, mediaInfo, config, workDir);
        if (asrTranscript is not null && asrTranscript.Segments.Count > 0)
        {
            result.SelectedSource = asrTranscript.Source;
            result.SelectionReason = "Subtitle resolution failed, so full-audio ASR transcript was selected.";
            result.SelectedTranscript = asrTranscript;
            result.Attempts.Add(new TranscriptResolutionAttempt
            {
                Kind = "full_audio_asr",
                Status = "selected",
                Reason = "Full-audio ASR produced a transcript.",
                Transcript = TranscriptArtifactService.Build(inputPath, asrTranscript),
            });
            result.Warnings.AddRange(asrTranscript.Warnings);
            return result;
        }

        result.Attempts.Add(new TranscriptResolutionAttempt
        {
            Kind = "full_audio_asr",
            Status = "failed",
            Reason = "Full-audio ASR fallback produced no transcript.",
            Transcript = asrTranscript is null ? null : TranscriptArtifactService.Build(inputPath, asrTranscript),
        });
        if (asrTranscript is not null)
        {
            result.Warnings.AddRange(asrTranscript.Warnings);
        }

        result.SelectionReason = "Subtitle resolution failed and ASR fallback produced no transcript.";
        return result;
    }

    private static async Task AddReferenceAsrAttemptAsync(TranscriptResolutionResult result, string inputPath, MediaInfo mediaInfo, AppConfig config, string workDir, TranscriptArtifact subtitleArtifact)
    {
        if (!config.Processing.AsrFallback)
        {
            result.Attempts.Add(new TranscriptResolutionAttempt
            {
                Kind = "full_audio_asr",
                Status = "disabled",
                Reason = "Full-audio ASR reference collection requires processing.asr_fallback to be enabled.",
            });
            return;
        }

        var asrTranscript = await AsrBridgeService.TranscribeFullAudioAsync(inputPath, mediaInfo, config, workDir);
        if (asrTranscript is null || asrTranscript.Segments.Count == 0)
        {
            result.Attempts.Add(new TranscriptResolutionAttempt
            {
                Kind = "full_audio_asr",
                Status = "failed",
                Reason = "Full-audio ASR reference collection produced no transcript.",
                Transcript = asrTranscript is null ? null : TranscriptArtifactService.Build(inputPath, asrTranscript),
            });

            if (asrTranscript is not null)
            {
                result.Warnings.AddRange(asrTranscript.Warnings);
            }

            return;
        }

        var asrArtifact = TranscriptArtifactService.Build(inputPath, asrTranscript);
        result.Attempts.Add(new TranscriptResolutionAttempt
        {
            Kind = "full_audio_asr",
            Status = "reference_collected",
            Reason = "Full-audio ASR transcript was collected as a comparison reference.",
            Transcript = asrArtifact,
        });
        result.Warnings.AddRange(asrTranscript.Warnings);
        result.Comparisons.Add(new TranscriptResolutionComparison
        {
            ReferenceKind = "subtitle",
            CandidateKind = "full_audio_asr",
            Result = TranscriptComparisonService.Compare(null, subtitleArtifact, null, asrArtifact),
        });
    }
}