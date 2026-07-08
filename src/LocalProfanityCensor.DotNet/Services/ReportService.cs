using System.Globalization;
using System.Text.Json;
using LocalProfanityCensor.DotNet.Models;

namespace LocalProfanityCensor.DotNet.Services;

internal static class ReportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static async Task WriteArtifactsAsync(string workDir, MediaInfo mediaInfo, TranscriptResolutionResult? transcriptResolution, ProcessResult processResult)
    {
        await WriteJsonAsync(Path.Combine(workDir, "media_info.json"), mediaInfo);
        if (transcriptResolution is not null)
        {
            await WriteJsonAsync(Path.Combine(workDir, "transcript_resolution.json"), transcriptResolution);

            var alignmentRequest = AlignmentRequestService.Build(processResult.InputFile, transcriptResolution);
            if (alignmentRequest is not null)
            {
                await AlignmentRequestService.WriteAsync(Path.Combine(workDir, "alignment_request.json"), alignmentRequest);
                await AlignmentPrototypeService.WriteAsync(
                    Path.Combine(workDir, "aligned_transcript.prototype.json"),
                    AlignmentPrototypeService.Align(alignmentRequest));
            }
        }

        var transcript = transcriptResolution?.SelectedTranscript;
        if (transcript is not null)
        {
            await TranscriptArtifactService.WriteAsync(
                Path.Combine(workDir, "transcript.json"),
                TranscriptArtifactService.Build(processResult.InputFile, transcript));
        }

        await WriteJsonAsync(Path.Combine(workDir, "matches.json"), processResult.Matches);
        await WriteJsonAsync(Path.Combine(workDir, "censor_ranges.json"), processResult.Ranges);
        if (processResult.Refinement is not null)
        {
            await WriteJsonAsync(Path.Combine(workDir, "refinement.json"), processResult.Refinement);
        }
    }

    public static async Task WriteReportsAsync(string reportStem, ProcessResult processResult, MediaInfo mediaInfo, TranscriptResolutionResult? transcriptResolution, AppConfig config)
    {
        if (config.Reports.JsonEnabled)
        {
            await WriteJsonAsync(reportStem + ".report.json", BuildJsonReport(processResult, mediaInfo, transcriptResolution, config));
        }

        if (config.Reports.CsvEnabled)
        {
            await WriteCsvReportAsync(reportStem + ".report.csv", processResult);
        }
    }

    private static object BuildJsonReport(ProcessResult processResult, MediaInfo mediaInfo, TranscriptResolutionResult? transcriptResolution, AppConfig config)
    {
        var totalCensoredSeconds = processResult.Ranges.Sum(item => Math.Max(0.0, item.End - item.Start));
        var transcript = transcriptResolution?.SelectedTranscript;
        var alignmentRequest = AlignmentRequestService.Build(processResult.InputFile, transcriptResolution);
        return new
        {
            input_file = processResult.InputFile,
            output_file = processResult.OutputFile,
            clean_audio_file = processResult.CleanAudioFile,
            status = processResult.Status,
            duration_seconds = processResult.DurationSeconds,
            text_source = processResult.TextSource,
            subtitle_stream = transcript?.Candidate,
            transcript_resolution = transcriptResolution,
            alignment_request = alignmentRequest,
            censor_config = new
            {
                mode = config.Censor.Mode,
                padding_start_ms = config.Censor.PaddingStartMs,
                padding_end_ms = config.Censor.PaddingEndMs,
                merge_gap_ms = config.Censor.MergeGapMs,
            },
            media = mediaInfo,
            summary = new
            {
                matches = processResult.Matches.Count,
                ranges = processResult.Ranges.Count,
                total_censored_seconds = Math.Round(totalCensoredSeconds, 3),
                refinement_windows = processResult.Refinement?.Windows.Count ?? 0,
            },
            matches = processResult.Matches,
            refinement = processResult.Refinement,
            ranges = processResult.Ranges.Select(item => new
            {
                start = Math.Round(item.Start, 3),
                end = Math.Round(item.End, 3),
                action = item.Action,
                match_count = item.Matches.Count,
            }),
            warnings = processResult.Warnings,
            failure_reason = processResult.FailureReason,
            message = processResult.Message,
        };
    }

    private static async Task WriteCsvReportAsync(string path, ProcessResult processResult)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory());
        await using var writer = new StreamWriter(path, false);
        await writer.WriteLineAsync("file,status,text_source,matched_text,replacement_text,term_id,severity,start,end,action,confidence,context_before,context_after");
        if (processResult.Matches.Count == 0)
        {
            await writer.WriteLineAsync(CsvLine(
                processResult.InputFile,
                processResult.Status,
                processResult.TextSource ?? string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty));
            return;
        }

        foreach (var match in processResult.Matches)
        {
            await writer.WriteLineAsync(CsvLine(
                processResult.InputFile,
                processResult.Status,
                processResult.TextSource ?? string.Empty,
                match.MatchedText,
                match.ReplacementText ?? string.Empty,
                match.TermId,
                match.Severity,
                Math.Round(match.Start, 3).ToString("F3", CultureInfo.InvariantCulture),
                Math.Round(match.End, 3).ToString("F3", CultureInfo.InvariantCulture),
                match.Action,
                match.Confidence?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                match.ContextBefore,
                match.ContextAfter));
        }
    }

    private static async Task WriteJsonAsync(string path, object payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory());
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static string CsvLine(params string[] values)
    {
        return string.Join(',', values.Select(EscapeCsv));
    }

    private static string EscapeCsv(string value)
    {
        var escaped = value.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }
}