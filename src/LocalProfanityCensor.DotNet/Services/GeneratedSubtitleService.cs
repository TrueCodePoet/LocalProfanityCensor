using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LocalProfanityCensor.DotNet.Models;

namespace LocalProfanityCensor.DotNet.Services;

internal static class GeneratedSubtitleService
{
    public static async Task<List<GeneratedSubtitleTrack>> CreateTracksAsync(
        TranscriptResult transcript,
        IReadOnlyList<ProfanityMatch> matches,
        MediaInfo mediaInfo,
        AppConfig config,
        string workDir,
        bool includeCensoredTrack = true)
    {
        if (!config.Subtitles.GenerateCensoredSubtitle || mediaInfo.VideoStreams.Count == 0)
        {
            return [];
        }

        Directory.CreateDirectory(workDir);
        var retainedSubtitles = MediaRenderService.GetRetainedSubtitleStreams(mediaInfo);
        var hasRetainedNormalSubtitle = retainedSubtitles.Any(item => !item.Stream.IsForced);
        var tracks = new List<GeneratedSubtitleTrack>();
        var language = ResolveLanguage(transcript, config);
        var generatePlainSubtitle = config.Subtitles.GeneratePlainSubtitleIfMissing && !hasRetainedNormalSubtitle;

        if (generatePlainSubtitle)
        {
            var normalPath = Path.Combine(workDir, "generated.transcript.srt");
            await WriteSrtAsync(normalPath, transcript.Segments, matches, censored: false, config);
            tracks.Add(new GeneratedSubtitleTrack
            {
                Path = normalPath,
                Title = "Transcription",
                Language = language,
                IsDefault = !config.Subtitles.DefaultCensoredSubtitle,
                IsCensored = false,
            });
        }

        if (includeCensoredTrack)
        {
            var censoredPath = Path.Combine(workDir, "generated.transcript.censored.srt");
            await WriteSrtAsync(censoredPath, transcript.Segments, matches, censored: true, config);
            tracks.Add(new GeneratedSubtitleTrack
            {
                Path = censoredPath,
                Title = "Transcription Censored",
                Language = language,
                IsDefault = config.Subtitles.DefaultCensoredSubtitle,
                IsCensored = true,
            });
        }

        if (includeCensoredTrack && config.Subtitles.DefaultCensoredSubtitle)
        {
            foreach (var track in tracks.Where(track => !track.IsCensored))
            {
                track.IsDefault = false;
            }
        }

        return tracks;
    }

    private static async Task WriteSrtAsync(
        string outputPath,
        IReadOnlyList<TranscriptSegment> segments,
        IReadOnlyList<ProfanityMatch> matches,
        bool censored,
        AppConfig config)
    {
        var builder = new StringBuilder();
        var cueIndex = 1;

        foreach (var segment in segments.OrderBy(item => item.Start))
        {
            var text = censored
                ? BuildCensoredSegmentText(segment, matches, config)
                : BuildSegmentText(segment);

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            builder.AppendLine(cueIndex.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine($"{FormatSrtTimestamp(segment.Start)} --> {FormatSrtTimestamp(segment.End)}");
            builder.AppendLine(text.Trim());
            builder.AppendLine();
            cueIndex++;
        }

        await File.WriteAllTextAsync(outputPath, builder.ToString());
    }

    private static string BuildSegmentText(TranscriptSegment segment)
    {
        if (segment.Words.Count > 0)
        {
            var concatenated = string.Join(" ", segment.Words.Select(word => word.Text.Trim()))
                .Trim();
            if (!string.IsNullOrWhiteSpace(concatenated))
            {
                return concatenated;
            }
        }

        return segment.Text.Trim();
    }

    private static string BuildCensoredSegmentText(
        TranscriptSegment segment,
        IReadOnlyList<ProfanityMatch> matches,
        AppConfig config)
    {
        var overlappingMatches = matches
            .Where(match => segment.Start < match.End && segment.End > match.Start)
            .ToList();

        if (segment.Words.Count == 0)
        {
            var replacement = ResolveSegmentReplacementText(segment, overlappingMatches, config);
            var segmentText = replacement ?? segment.Text.Trim();
            return ApplySegmentTextFallbackCensoring(segmentText, segment, overlappingMatches, config);
        }

        var parts = new List<string>(segment.Words.Count);
        for (var index = 0; index < segment.Words.Count; index++)
        {
            var word = segment.Words[index];
            var replacement = ResolveWordReplacementText(word, overlappingMatches, config);
            if (replacement is null)
            {
                parts.Add(word.Text.Trim());
            }
            else if (replacement.Length > 0)
            {
                parts.Add(replacement);
            }
        }

        var text = string.Join(" ", parts).Trim();
        return ApplySegmentTextFallbackCensoring(text, segment, overlappingMatches, config);
    }

    private static string ApplySegmentTextFallbackCensoring(
        string text,
        TranscriptSegment segment,
        IReadOnlyList<ProfanityMatch> matches,
        AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var overlappingMatches = matches
            .Where(match => segment.Start < match.End && segment.End > match.Start)
            .OrderByDescending(match => (match.NormalizedText ?? string.Empty).Length)
            .ToList();

        if (overlappingMatches.Count == 0)
        {
            return text;
        }

        var updated = text;
        foreach (var match in overlappingMatches)
        {
            var replacement = ResolveReplacementText(match, config);
            var normalized = (match.NormalizedText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            var pattern = BuildLoosePhrasePattern(normalized);
            updated = Regex.Replace(
                updated,
                pattern,
                replacement ?? string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        updated = Regex.Replace(updated, "\\s{2,}", " ").Trim();
        return updated;
    }

    private static string BuildLoosePhrasePattern(string normalizedPhrase)
    {
        var tokens = normalizedPhrase
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Regex.Escape)
            .ToArray();

        if (tokens.Length == 0)
        {
            return "$a";
        }

        return $"\\b{string.Join("\\W+", tokens)}\\b";
    }

    private static bool SegmentOverlapsMatch(TranscriptSegment segment, IReadOnlyList<ProfanityMatch> matches)
    {
        return matches.Any(match => segment.Start < match.End && segment.End > match.Start);
    }

    private static bool WordOverlapsMatch(TranscriptWord word, IReadOnlyList<ProfanityMatch> matches)
    {
        return matches.Any(match => word.Start < match.End && word.End > match.Start);
    }

    private static string? ResolveSegmentReplacementText(
        TranscriptSegment segment,
        IReadOnlyList<ProfanityMatch> matches,
        AppConfig config)
    {
        var overlappingMatch = matches.FirstOrDefault();
        return overlappingMatch is null
            ? null
            : ResolveReplacementText(overlappingMatch, config);
    }

    private static string? ResolveWordReplacementText(
        TranscriptWord word,
        IReadOnlyList<ProfanityMatch> matches,
        AppConfig config)
    {
        var overlappingMatch = matches.FirstOrDefault(match => word.Start < match.End && word.End > match.Start)
            ?? matches.FirstOrDefault(match =>
                string.Equals(word.Normalized, match.NormalizedText, StringComparison.OrdinalIgnoreCase)
                || (match.NormalizedText?.Contains(word.Normalized, StringComparison.OrdinalIgnoreCase) ?? false));
        return overlappingMatch is null
            ? null
            : ResolveReplacementText(overlappingMatch, config);
    }

    private static string? ResolveReplacementText(ProfanityMatch match, AppConfig config)
    {
        var effectiveAction = config.Censor.Mode;

        return effectiveAction.ToLowerInvariant() switch
        {
            "beep" => "[CENSORED]",
            "mute" => string.Empty,
            "silence" => string.Empty,
            "replace" => string.IsNullOrWhiteSpace(match.ReplacementText) ? string.Empty : match.ReplacementText.Trim(),
            _ => null,
        };
    }

    private static string ResolveLanguage(TranscriptResult transcript, AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(transcript.Candidate?.Language))
        {
            return transcript.Candidate.Language;
        }

        return string.IsNullOrWhiteSpace(config.Processing.Language) ? "en" : config.Processing.Language;
    }

    private static string FormatSrtTimestamp(double seconds)
    {
        var clamped = Math.Max(0, seconds);
        var timeSpan = TimeSpan.FromSeconds(clamped);
        var totalHours = (int)timeSpan.TotalHours;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00},{3:000}",
            totalHours,
            timeSpan.Minutes,
            timeSpan.Seconds,
            timeSpan.Milliseconds);
    }
}