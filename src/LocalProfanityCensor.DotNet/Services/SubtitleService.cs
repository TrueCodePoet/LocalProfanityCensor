using System.Net;
using System.Text.RegularExpressions;
using LocalProfanityCensor.DotNet.Models;

namespace LocalProfanityCensor.DotNet.Services;

internal static partial class SubtitleService
{
    public static async Task<TranscriptResult?> ResolveTranscriptAsync(string inputPath, MediaInfo mediaInfo, AppConfig config, string workDir)
    {
        if (!config.Processing.CcFirst)
        {
            return null;
        }

        foreach (var candidate in SelectSubtitleCandidates(inputPath, mediaInfo, config))
        {
            var subtitlePath = await MaterializeSubtitleCandidateAsync(inputPath, candidate, workDir);
            var source = string.Equals(candidate.SourceType, "embedded", StringComparison.OrdinalIgnoreCase)
                ? "embedded_subtitle"
                : "sidecar_subtitle";
            var transcript = ParseSubtitleFile(subtitlePath, source);
            ApplyTimingOffset(transcript.Segments, config.Subtitles.TimingOffsetMs / 1000.0);
            transcript.Candidate = candidate;

            var (score, warnings) = ScoreSubtitleTranscript(transcript.Segments, mediaInfo, candidate.Language, config);
            transcript.Warnings.AddRange(warnings);
            if (score >= config.Subtitles.MinScore)
            {
                return transcript;
            }
        }

        return null;
    }

    public static List<SubtitleCandidate> SelectSubtitleCandidates(string inputPath, MediaInfo mediaInfo, AppConfig config)
    {
        var allowedLanguages = new HashSet<string>(config.Subtitles.AllowedLanguages.Select(item => item.ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        var candidates = new List<SubtitleCandidate>();

        if (config.Subtitles.PreferSidecar)
        {
            foreach (var subtitlePath in mediaInfo.SidecarSubtitles)
            {
                var language = GuessLanguage(subtitlePath);
                var scoreHint = 40;
                if (!string.IsNullOrWhiteSpace(language) && allowedLanguages.Contains(language))
                {
                    scoreHint += 20;
                }

                candidates.Add(new SubtitleCandidate
                {
                    SourceType = "sidecar",
                    Path = subtitlePath,
                    Language = language,
                    Title = Path.GetFileName(subtitlePath),
                    ScoreHint = scoreHint,
                });
            }
        }

        if (config.Subtitles.PreferEmbedded)
        {
            foreach (var stream in mediaInfo.SubtitleStreams)
            {
                var codecName = (stream.CodecName ?? string.Empty).ToLowerInvariant();
                var language = (stream.Language ?? "und").ToLowerInvariant();
                var title = (stream.Title ?? string.Empty).ToLowerInvariant();
                var scoreHint = 50;

                if (TextSubtitleCodecs.Contains(codecName))
                {
                    if (allowedLanguages.Contains(language))
                    {
                        scoreHint += 20;
                    }

                    if (!string.IsNullOrWhiteSpace(title) && !title.Contains("forced", StringComparison.Ordinal) && !title.Contains("sdh", StringComparison.Ordinal) && !title.Contains("commentary", StringComparison.Ordinal))
                    {
                        scoreHint += 10;
                    }

                    if (title.Contains("forced", StringComparison.Ordinal))
                    {
                        scoreHint -= 20;
                    }

                    if (title.Contains("sdh", StringComparison.Ordinal) || title.Contains("hearing", StringComparison.Ordinal))
                    {
                        scoreHint -= 10;
                    }

                    if (title.Contains("commentary", StringComparison.Ordinal))
                    {
                        scoreHint -= 15;
                    }

                    candidates.Add(new SubtitleCandidate
                    {
                        SourceType = "embedded",
                        StreamIndex = stream.Index,
                        CodecName = stream.CodecName,
                        Language = stream.Language,
                        Title = stream.Title,
                        ScoreHint = scoreHint,
                    });
                }
            }
        }

        return candidates
            .OrderByDescending(item => item.ScoreHint)
            .ThenByDescending(item => item.StreamIndex ?? 0)
            .ToList();
    }

    public static TranscriptResult ParseSubtitleFile(string path, string source)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".srt" => new TranscriptResult { Source = source, Segments = ParseSrt(path, source) },
            ".vtt" => new TranscriptResult { Source = source, Segments = ParseVtt(path, source) },
            _ => throw new InvalidOperationException($"Unsupported subtitle format: {path}"),
        };
    }

    private static readonly HashSet<string> TextSubtitleCodecs =
    [
        "mov_text",
        "subrip",
        "webvtt",
        "ass",
        "ssa",
        "eia_608",
    ];

    private static List<TranscriptSegment> ParseSrt(string path, string source)
    {
        var content = File.ReadAllText(path);
        var normalizedContent = content.Replace("\r\n", "\n");
        var blocks = Regex.Split(normalizedContent.Trim(), "\\n\\s*\\n");
        var segments = new List<TranscriptSegment>();

        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2)
            {
                continue;
            }

            var timeLineIndex = lines[0].Contains("-->", StringComparison.Ordinal) ? 0 : 1;
            if (timeLineIndex >= lines.Length || !lines[timeLineIndex].Contains("-->", StringComparison.Ordinal))
            {
                continue;
            }

            var times = lines[timeLineIndex].Split("-->", StringSplitOptions.TrimEntries);
            if (times.Length != 2)
            {
                continue;
            }

            var text = CollapseText(string.Join(' ', lines.Skip(timeLineIndex + 1)));
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            segments.Add(BuildSegment(text, ParseSrtTime(times[0]), ParseSrtTime(times[1]), source));
        }

        return segments;
    }

    private static List<TranscriptSegment> ParseVtt(string path, string source)
    {
        var content = File.ReadAllText(path);
        var normalizedContent = content.Replace("\r\n", "\n");
        var blocks = Regex.Split(normalizedContent.Trim(), "\\n\\s*\\n");
        var segments = new List<TranscriptSegment>();

        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                continue;
            }

            if (lines[0].Equals("WEBVTT", StringComparison.OrdinalIgnoreCase) || lines[0].StartsWith("NOTE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var timeLineIndex = lines[0].Contains("-->", StringComparison.Ordinal) ? 0 : 1;
            if (timeLineIndex >= lines.Length || !lines[timeLineIndex].Contains("-->", StringComparison.Ordinal))
            {
                continue;
            }

            var times = lines[timeLineIndex].Split("-->", StringSplitOptions.TrimEntries);
            if (times.Length != 2)
            {
                continue;
            }

            var endTime = times[1].Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[0];
            var text = CollapseText(string.Join(' ', lines.Skip(timeLineIndex + 1)));
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            segments.Add(BuildSegment(text, ParseVttTime(times[0]), ParseVttTime(endTime), source));
        }

        return segments;
    }

    private static TranscriptSegment BuildSegment(string text, double start, double end, string source)
    {
        var tokens = TokenRegex().Matches(text)
            .Select(match => new
            {
                Text = match.Value,
                Normalized = TextNormalization.NormalizeToken(match.Value),
                StartIndex = match.Index,
                EndIndex = match.Index + match.Length,
            })
            .Where(token => !string.IsNullOrWhiteSpace(token.Normalized))
            .ToList();

        var duration = Math.Max(0.05, end - start);
        var words = new List<TranscriptWord>();
        if (tokens.Count > 0)
        {
            var totalCharacters = Math.Max(1, text.Length);
            for (var index = 0; index < tokens.Count; index++)
            {
                var token = tokens[index];
                var nextBoundary = index == tokens.Count - 1
                    ? totalCharacters
                    : Math.Max(token.EndIndex, tokens[index + 1].StartIndex);
                var wordStart = start + (duration * token.StartIndex / totalCharacters);
                var wordEnd = index == tokens.Count - 1
                    ? end
                    : start + (duration * nextBoundary / totalCharacters);
                words.Add(new TranscriptWord
                {
                    Text = token.Text,
                    Normalized = token.Normalized,
                    Start = wordStart,
                    End = Math.Max(wordStart + 0.01, Math.Min(end, wordEnd)),
                    Source = source,
                    TimingSource = "subtitle_estimated",
                });
            }
        }

        return new TranscriptSegment
        {
            Text = TextNormalization.NormalizeText(text, preserveCase: true),
            Start = start,
            End = end,
            Source = source,
            Words = words,
        };
    }

    private static void ApplyTimingOffset(List<TranscriptSegment> segments, double offsetSeconds)
    {
        if (segments.Count == 0 || Math.Abs(offsetSeconds) < 0.0001)
        {
            return;
        }

        foreach (var segment in segments)
        {
            segment.Start = Math.Max(0.0, segment.Start + offsetSeconds);
            segment.End = Math.Max(segment.Start + 0.05, segment.End + offsetSeconds);

            foreach (var word in segment.Words)
            {
                word.Start = Math.Max(0.0, word.Start + offsetSeconds);
                word.End = Math.Max(word.Start + 0.01, word.End + offsetSeconds);
            }
        }
    }

    private static (int Score, List<string> Warnings) ScoreSubtitleTranscript(List<TranscriptSegment> segments, MediaInfo mediaInfo, string? language, AppConfig config)
    {
        var score = 0;
        var warnings = new List<string>();
        if (segments.Count > 0)
        {
            score += 30;
        }
        else
        {
            warnings.Add("Subtitle candidate contains no usable cues.");
        }

        var requestedLanguage = config.Processing.Language.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(language) && new[] { requestedLanguage, "eng", "en" }.Contains(language.ToLowerInvariant(), StringComparer.Ordinal))
        {
            score += 20;
        }

        if (segments.Count >= config.Subtitles.MinCaptionCount)
        {
            score += 15;
        }
        else
        {
            warnings.Add($"Subtitle candidate has only {segments.Count} cue(s); expected at least {config.Subtitles.MinCaptionCount}.");
        }

        if (mediaInfo.DurationSeconds > 0 && segments.Count > 0)
        {
            var coverage = (segments[^1].End - segments[0].Start) / mediaInfo.DurationSeconds;
            if (coverage >= 0.5)
            {
                score += 20;
            }
            else
            {
                warnings.Add($"Subtitle coverage ratio is low: {coverage:F2}");
            }
        }

        var joinedText = string.Join(' ', segments.Select(segment => segment.Text));
        if (!string.IsNullOrWhiteSpace(joinedText))
        {
            score += 10;
        }

        if (joinedText.Contains('*', StringComparison.Ordinal) || joinedText.Contains("[bleep]", StringComparison.OrdinalIgnoreCase))
        {
            score -= 20;
            warnings.Add("Subtitle candidate appears partially sanitized.");
        }

        return (score, warnings);
    }

    private static string? GuessLanguage(string path)
    {
        var lowerName = Path.GetFileName(path).ToLowerInvariant();
        if (lowerName.Contains(".eng.", StringComparison.Ordinal))
        {
            return "eng";
        }

        if (lowerName.Contains(".en.", StringComparison.Ordinal))
        {
            return "en";
        }

        return null;
    }

    private static string CollapseText(string text)
    {
        var withoutMarkup = HtmlTagRegex().Replace(WebUtility.HtmlDecode(text), " ");
        return MultiWhitespaceRegex().Replace(withoutMarkup.Replace("\n", " "), " ").Trim();
    }

    private static double ParseSrtTime(string value)
    {
        var parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            throw new InvalidOperationException($"Invalid SRT timestamp: {value}");
        }

        var secondsParts = parts[2].Split(',', StringSplitOptions.TrimEntries);
        var wholeSeconds = int.Parse(secondsParts[0]);
        var milliseconds = secondsParts.Length > 1 ? int.Parse(secondsParts[1]) : 0;
        return (int.Parse(parts[0]) * 3600) + (int.Parse(parts[1]) * 60) + wholeSeconds + (milliseconds / 1000.0);
    }

    private static double ParseVttTime(string value)
    {
        var parts = value.Split(':', StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            3 => (int.Parse(parts[0]) * 3600) + (int.Parse(parts[1]) * 60) + double.Parse(parts[2]),
            2 => (int.Parse(parts[0]) * 60) + double.Parse(parts[1]),
            _ => throw new InvalidOperationException($"Invalid VTT timestamp: {value}"),
        };
    }

    [GeneratedRegex("\\S+")]
    private static partial Regex TokenRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex MultiWhitespaceRegex();

    private static async Task<string> MaterializeSubtitleCandidateAsync(string inputMedia, SubtitleCandidate candidate, string workDir)
    {
        if (string.Equals(candidate.SourceType, "sidecar", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(candidate.Path))
            {
                throw new InvalidOperationException("Sidecar subtitle candidate is missing a path.");
            }

            return candidate.Path;
        }

        if (!candidate.StreamIndex.HasValue)
        {
            throw new InvalidOperationException("Embedded subtitle candidate is missing a stream index.");
        }

        ToolRunner.EnsureToolExists("ffmpeg");
        Directory.CreateDirectory(workDir);
        var baseName = $"extracted-subtitle-{candidate.StreamIndex.Value}";
        var attempts = new[]
        {
            Path.Combine(workDir, baseName + ".srt"),
            Path.Combine(workDir, baseName + ".vtt"),
        };

        foreach (var outputPath in attempts)
        {
            try
            {
                await ToolRunner.RunAsync(
                    "ffmpeg",
                    "-y",
                    "-i",
                    inputMedia,
                    "-map",
                    $"0:{candidate.StreamIndex.Value}",
                    outputPath);
                return outputPath;
            }
            catch (InvalidOperationException)
            {
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
            }
        }

        throw new InvalidOperationException($"Unable to extract subtitle stream {candidate.StreamIndex.Value} from {inputMedia}");
    }
}