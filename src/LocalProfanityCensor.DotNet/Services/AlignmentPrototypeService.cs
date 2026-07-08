using System.Text.Json;
using LocalProfanityCensor.DotNet.Models;

namespace LocalProfanityCensor.DotNet.Services;

internal static class AlignmentPrototypeService
{
    private const int MaxPhraseWords = 3;
    private const double MinimumWordDurationSeconds = 0.01;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = true,
    };

    public static AlignmentPrototypeResult Align(TranscriptAlignmentRequest request)
    {
        var selectedEntries = FlattenWords(request.SelectedTranscript);
        var selectedWordCount = selectedEntries.Count;

        if (request.ReferenceTranscript is null)
        {
            return BuildNoReferenceResult(request, selectedWordCount);
        }

        var referenceEntries = FlattenWords(request.ReferenceTranscript);
        if (referenceEntries.Count == 0)
        {
            var noReferenceResult = BuildNoReferenceResult(request, selectedWordCount);
            noReferenceResult.ReferenceKind = request.ReferenceKind;
            noReferenceResult.Summary.ReferenceWordCount = 0;
            noReferenceResult.Warnings.Add("Reference transcript did not contain any words, so prototype alignment kept the selected transcript timings.");
            return noReferenceResult;
        }

        var alignedSegments = CloneSegments(request.SelectedTranscript.Segments);
        var matches = BuildMatchSpans(selectedEntries, referenceEntries);
        var matchedWordCount = 0;
        var adoptedTimingWordCount = 0;
        var phraseAlignedWordCount = 0;
        var interpolatedTimingWordCount = 0;
        var alignedSelectedIndices = new SortedSet<int>();

        foreach (var match in matches)
        {
            matchedWordCount += match.SelectedLength;

            if (match.SelectedLength == 1 && match.ReferenceLength == 1)
            {
                var selected = selectedEntries[match.SelectedStartIndex];
                var reference = referenceEntries[match.ReferenceStartIndex];
                AdoptReferenceTiming(alignedSegments[selected.SegmentIndex].Words[selected.WordIndex], reference.Word, request.ReferenceKind);
                adoptedTimingWordCount++;
                alignedSelectedIndices.Add(match.SelectedStartIndex);
                continue;
            }

            phraseAlignedWordCount += ApplyPhraseTiming(alignedSegments, selectedEntries, referenceEntries, match, request.ReferenceKind, alignedSelectedIndices);
        }

        interpolatedTimingWordCount += InterpolateInteriorUnmatchedWords(alignedSegments, selectedEntries, alignedSelectedIndices, request.ReferenceKind);
        interpolatedTimingWordCount += InterpolateEdgeUnmatchedWords(alignedSegments, selectedEntries, alignedSelectedIndices, request.ReferenceKind);

        RecomputeSegmentBounds(alignedSegments);
        var transcript = TranscriptArtifactService.Build(
            request.InputFile,
            new TranscriptResult
            {
                Source = $"prototype_aligned_{request.SelectedTranscript.Source}",
                Candidate = request.SelectedTranscript.Candidate,
                Segments = alignedSegments,
                Warnings = BuildTranscriptWarnings(request),
            });

        var result = new AlignmentPrototypeResult
        {
            InputFile = request.InputFile,
            Status = matchedWordCount > 0 ? "completed" : "completed_with_fallback",
            Strategy = request.Strategy,
            SelectedKind = request.SelectedKind,
            ReferenceKind = request.ReferenceKind,
            Transcript = transcript,
            Summary = new AlignmentPrototypeSummary
            {
                SelectedWordCount = selectedWordCount,
                ReferenceWordCount = referenceEntries.Count,
                MatchedWordCount = matchedWordCount,
                AdoptedTimingWordCount = adoptedTimingWordCount,
                PhraseAlignedWordCount = phraseAlignedWordCount,
                InterpolatedTimingWordCount = interpolatedTimingWordCount,
                UnmatchedSelectedWordCount = Math.Max(0, selectedWordCount - matchedWordCount),
            },
            Warnings = BuildResultWarnings(request, matchedWordCount, selectedWordCount, phraseAlignedWordCount, interpolatedTimingWordCount),
        };

        return result;
    }

    public static async Task WriteAsync(string path, AlignmentPrototypeResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory());
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, JsonOptions));
    }

    private static AlignmentPrototypeResult BuildNoReferenceResult(TranscriptAlignmentRequest request, int selectedWordCount)
    {
        return new AlignmentPrototypeResult
        {
            InputFile = request.InputFile,
            Status = "no_reference",
            Strategy = request.Strategy,
            SelectedKind = request.SelectedKind,
            ReferenceKind = request.ReferenceKind,
            Transcript = request.SelectedTranscript,
            Summary = new AlignmentPrototypeSummary
            {
                SelectedWordCount = selectedWordCount,
                ReferenceWordCount = 0,
                MatchedWordCount = 0,
                AdoptedTimingWordCount = 0,
                PhraseAlignedWordCount = 0,
                InterpolatedTimingWordCount = 0,
                UnmatchedSelectedWordCount = selectedWordCount,
            },
            Warnings = ["No reference transcript was available, so prototype alignment returned the selected transcript unchanged."],
        };
    }

    private static List<WordEntry> FlattenWords(TranscriptArtifact artifact)
    {
        var entries = new List<WordEntry>();
        for (var segmentIndex = 0; segmentIndex < artifact.Segments.Count; segmentIndex++)
        {
            var segment = artifact.Segments[segmentIndex];
            for (var wordIndex = 0; wordIndex < segment.Words.Count; wordIndex++)
            {
                var word = segment.Words[wordIndex];
                if (!string.IsNullOrWhiteSpace(word.Normalized))
                {
                    entries.Add(new WordEntry(segmentIndex, wordIndex, word));
                }
            }
        }

        return entries;
    }

    private static List<TranscriptSegment> CloneSegments(IEnumerable<TranscriptSegment> segments)
    {
        return segments.Select(segment => new TranscriptSegment
        {
            Text = segment.Text,
            Start = segment.Start,
            End = segment.End,
            Source = segment.Source,
            Words = segment.Words.Select(word => new TranscriptWord
            {
                Text = word.Text,
                Normalized = word.Normalized,
                Start = word.Start,
                End = word.End,
                Confidence = word.Confidence,
                Source = word.Source,
                TimingSource = word.TimingSource,
                AlignmentSource = word.AlignmentSource,
            }).ToList(),
        }).ToList();
    }

    private static void RecomputeSegmentBounds(List<TranscriptSegment> segments)
    {
        foreach (var segment in segments)
        {
            if (segment.Words.Count == 0)
            {
                continue;
            }

            segment.Start = segment.Words.Min(word => word.Start);
            segment.End = segment.Words.Max(word => word.End);
        }
    }

    private static bool WordsMatch(TranscriptWord selected, TranscriptWord reference)
    {
        return string.Equals(selected.Normalized, reference.Normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static int ApplyPhraseTiming(
        IReadOnlyList<TranscriptSegment> alignedSegments,
        IReadOnlyList<WordEntry> selectedEntries,
        IReadOnlyList<WordEntry> referenceEntries,
        MatchedSpan match,
        string? referenceKind,
        ISet<int> alignedSelectedIndices)
    {
        var referenceStart = referenceEntries[match.ReferenceStartIndex].Word.Start;
        var referenceEnd = referenceEntries[match.ReferenceStartIndex + match.ReferenceLength - 1].Word.End;
        var selectedSpanEntries = selectedEntries.Skip(match.SelectedStartIndex).Take(match.SelectedLength).ToList();
        var totalWeight = selectedSpanEntries.Sum(entry => Math.Max(0.01, entry.Word.End - entry.Word.Start));
        var cursor = referenceStart;

        for (var index = 0; index < selectedSpanEntries.Count; index++)
        {
            var entry = selectedSpanEntries[index];
            var targetWord = alignedSegments[entry.SegmentIndex].Words[entry.WordIndex];
            var duration = index == selectedSpanEntries.Count - 1
                ? Math.Max(0.01, referenceEnd - cursor)
                : Math.Max(0.01, (referenceEnd - referenceStart) * (Math.Max(0.01, entry.Word.End - entry.Word.Start) / totalWeight));
            var nextCursor = Math.Min(referenceEnd, cursor + duration);

            ApplyClampedTiming(targetWord, cursor, nextCursor);
            targetWord.TimingSource = "prototype_phrase_aligned";
            targetWord.AlignmentSource = string.IsNullOrWhiteSpace(referenceKind)
                ? "prototype_phrase_match"
                : $"prototype_phrase_match:{referenceKind}";

            cursor = nextCursor;
            alignedSelectedIndices.Add(match.SelectedStartIndex + index);
        }

        return match.SelectedLength;
    }

    private static int InterpolateInteriorUnmatchedWords(
        IReadOnlyList<TranscriptSegment> alignedSegments,
        IReadOnlyList<WordEntry> selectedEntries,
        IEnumerable<int> alignedSelectedIndices,
        string? referenceKind)
    {
        var anchors = alignedSelectedIndices.OrderBy(index => index).ToList();
        if (anchors.Count < 2)
        {
            return 0;
        }

        var interpolatedCount = 0;
        for (var anchorIndex = 0; anchorIndex < anchors.Count - 1; anchorIndex++)
        {
            var currentAnchorIndex = anchors[anchorIndex];
            var nextAnchorIndex = anchors[anchorIndex + 1];
            var gapStartIndex = currentAnchorIndex + 1;
            var gapEndIndex = nextAnchorIndex - 1;
            if (gapStartIndex > gapEndIndex)
            {
                continue;
            }

            var leftAnchorEntry = selectedEntries[currentAnchorIndex];
            var rightAnchorEntry = selectedEntries[nextAnchorIndex];
            var leftAnchor = alignedSegments[leftAnchorEntry.SegmentIndex].Words[leftAnchorEntry.WordIndex];
            var rightAnchor = alignedSegments[rightAnchorEntry.SegmentIndex].Words[rightAnchorEntry.WordIndex];
            var availableDuration = rightAnchor.End - leftAnchor.End;
            if (availableDuration <= 0)
            {
                continue;
            }

            var redistributedEntries = new List<RedistributedEntry>();
            for (var selectedIndex = gapStartIndex; selectedIndex <= gapEndIndex; selectedIndex++)
            {
                var entry = selectedEntries[selectedIndex];
                redistributedEntries.Add(new RedistributedEntry(entry, false, Math.Max(0.01, entry.Word.End - entry.Word.Start)));
            }

            redistributedEntries.Add(new RedistributedEntry(rightAnchorEntry, true, Math.Max(0.01, rightAnchor.End - rightAnchor.Start)));
            var totalOriginalDuration = redistributedEntries.Sum(entry => entry.DurationWeight);
            var intervalEnd = rightAnchor.End;
            var cursor = leftAnchor.End;

            for (var index = 0; index < redistributedEntries.Count; index++)
            {
                var entry = redistributedEntries[index];
                var targetWord = alignedSegments[entry.WordEntry.SegmentIndex].Words[entry.WordEntry.WordIndex];
                var segmentDuration = index == redistributedEntries.Count - 1
                    ? Math.Max(0.01, intervalEnd - cursor)
                    : Math.Max(0.01, availableDuration * (entry.DurationWeight / totalOriginalDuration));
                var nextCursor = Math.Min(intervalEnd, cursor + segmentDuration);

                if (entry.IsRightAnchor)
                {
                    ApplyClampedTiming(targetWord, cursor, nextCursor);
                    targetWord.AlignmentSource = string.IsNullOrWhiteSpace(referenceKind)
                        ? "prototype_reference_match_adjusted"
                        : $"prototype_reference_match_adjusted:{referenceKind}";
                }
                else
                {
                    ApplyClampedTiming(targetWord, cursor, nextCursor);
                    targetWord.TimingSource = "prototype_interpolated";
                    targetWord.AlignmentSource = string.IsNullOrWhiteSpace(referenceKind)
                        ? "prototype_interpolated_between_matches"
                        : $"prototype_interpolated_between_matches:{referenceKind}";
                    interpolatedCount++;
                }

                cursor = nextCursor;
            }
        }

        return interpolatedCount;
    }

    private static int InterpolateEdgeUnmatchedWords(
        IReadOnlyList<TranscriptSegment> alignedSegments,
        IReadOnlyList<WordEntry> selectedEntries,
        IEnumerable<int> alignedSelectedIndices,
        string? referenceKind)
    {
        var anchors = alignedSelectedIndices.OrderBy(index => index).ToList();
        if (anchors.Count == 0)
        {
            return 0;
        }

        var interpolatedCount = 0;
        interpolatedCount += InterpolateLeadingEdge(alignedSegments, selectedEntries, anchors[0], referenceKind);
        interpolatedCount += InterpolateTrailingEdge(alignedSegments, selectedEntries, anchors[^1], referenceKind);
        return interpolatedCount;
    }

    private static int InterpolateLeadingEdge(
        IReadOnlyList<TranscriptSegment> alignedSegments,
        IReadOnlyList<WordEntry> selectedEntries,
        int firstAnchorIndex,
        string? referenceKind)
    {
        if (firstAnchorIndex <= 0)
        {
            return 0;
        }

        var firstAnchorEntry = selectedEntries[firstAnchorIndex];
        var firstAnchor = alignedSegments[firstAnchorEntry.SegmentIndex].Words[firstAnchorEntry.WordIndex];
        var redistributedEntries = new List<RedistributedEntry>();
        for (var index = 0; index < firstAnchorIndex; index++)
        {
            var entry = selectedEntries[index];
            redistributedEntries.Add(new RedistributedEntry(entry, false, Math.Max(0.01, entry.Word.End - entry.Word.Start)));
        }

        redistributedEntries.Add(new RedistributedEntry(firstAnchorEntry, true, Math.Max(0.01, firstAnchor.End - firstAnchor.Start)));
        var totalWeight = redistributedEntries.Sum(entry => entry.DurationWeight);
        var intervalEnd = firstAnchor.End;
        var cursor = Math.Max(0.0, intervalEnd - totalWeight);
        var interpolatedCount = 0;

        for (var index = 0; index < redistributedEntries.Count; index++)
        {
            var entry = redistributedEntries[index];
            var targetWord = alignedSegments[entry.WordEntry.SegmentIndex].Words[entry.WordEntry.WordIndex];
            var duration = index == redistributedEntries.Count - 1
                ? Math.Max(0.01, intervalEnd - cursor)
                : Math.Max(0.01, totalWeight * (entry.DurationWeight / totalWeight));
            var nextCursor = Math.Min(intervalEnd, cursor + duration);

            if (entry.IsRightAnchor)
            {
                ApplyClampedTiming(targetWord, cursor, nextCursor);
                targetWord.AlignmentSource = string.IsNullOrWhiteSpace(referenceKind)
                    ? "prototype_reference_match_adjusted_leading"
                    : $"prototype_reference_match_adjusted_leading:{referenceKind}";
            }
            else
            {
                ApplyClampedTiming(targetWord, cursor, nextCursor);
                targetWord.TimingSource = "prototype_interpolated";
                targetWord.AlignmentSource = string.IsNullOrWhiteSpace(referenceKind)
                    ? "prototype_interpolated_leading"
                    : $"prototype_interpolated_leading:{referenceKind}";
                interpolatedCount++;
            }

            cursor = nextCursor;
        }

        return interpolatedCount;
    }

    private static int InterpolateTrailingEdge(
        IReadOnlyList<TranscriptSegment> alignedSegments,
        IReadOnlyList<WordEntry> selectedEntries,
        int lastAnchorIndex,
        string? referenceKind)
    {
        if (lastAnchorIndex >= selectedEntries.Count - 1)
        {
            return 0;
        }

        var lastAnchorEntry = selectedEntries[lastAnchorIndex];
        var lastAnchor = alignedSegments[lastAnchorEntry.SegmentIndex].Words[lastAnchorEntry.WordIndex];
        var redistributedEntries = new List<RedistributedEntry>
        {
            new(lastAnchorEntry, true, Math.Max(0.01, lastAnchor.End - lastAnchor.Start))
        };

        for (var index = lastAnchorIndex + 1; index < selectedEntries.Count; index++)
        {
            var entry = selectedEntries[index];
            redistributedEntries.Add(new RedistributedEntry(entry, false, Math.Max(0.01, entry.Word.End - entry.Word.Start)));
        }

        var totalWeight = redistributedEntries.Sum(entry => entry.DurationWeight);
        var cursor = lastAnchor.Start;
        var intervalEnd = cursor + totalWeight;
        var interpolatedCount = 0;

        for (var index = 0; index < redistributedEntries.Count; index++)
        {
            var entry = redistributedEntries[index];
            var targetWord = alignedSegments[entry.WordEntry.SegmentIndex].Words[entry.WordEntry.WordIndex];
            var duration = index == redistributedEntries.Count - 1
                ? Math.Max(0.01, intervalEnd - cursor)
                : Math.Max(0.01, totalWeight * (entry.DurationWeight / totalWeight));
            var nextCursor = Math.Min(intervalEnd, cursor + duration);

            if (entry.IsRightAnchor)
            {
                ApplyClampedTiming(targetWord, cursor, nextCursor);
                targetWord.AlignmentSource = string.IsNullOrWhiteSpace(referenceKind)
                    ? "prototype_reference_match_adjusted_trailing"
                    : $"prototype_reference_match_adjusted_trailing:{referenceKind}";
            }
            else
            {
                ApplyClampedTiming(targetWord, cursor, nextCursor);
                targetWord.TimingSource = "prototype_interpolated";
                targetWord.AlignmentSource = string.IsNullOrWhiteSpace(referenceKind)
                    ? "prototype_interpolated_trailing"
                    : $"prototype_interpolated_trailing:{referenceKind}";
                interpolatedCount++;
            }

            cursor = nextCursor;
        }

        return interpolatedCount;
    }

    private static List<MatchedSpan> BuildMatchSpans(IReadOnlyList<WordEntry> selectedEntries, IReadOnlyList<WordEntry> referenceEntries)
    {
        if (selectedEntries.Count == 0 || referenceEntries.Count == 0)
        {
            return [];
        }

        var score = new int[selectedEntries.Count + 1, referenceEntries.Count + 1];
        for (var selectedIndex = selectedEntries.Count - 1; selectedIndex >= 0; selectedIndex--)
        {
            for (var referenceIndex = referenceEntries.Count - 1; referenceIndex >= 0; referenceIndex--)
            {
                var bestScore = Math.Max(score[selectedIndex + 1, referenceIndex], score[selectedIndex, referenceIndex + 1]);
                for (var selectedLength = 1; selectedLength <= MaxPhraseWords && selectedIndex + selectedLength <= selectedEntries.Count; selectedLength++)
                {
                    for (var referenceLength = 1; referenceLength <= MaxPhraseWords && referenceIndex + referenceLength <= referenceEntries.Count; referenceLength++)
                    {
                        if (!PhraseMatches(selectedEntries, selectedIndex, selectedLength, referenceEntries, referenceIndex, referenceLength))
                        {
                            continue;
                        }

                        var phraseScore = score[selectedIndex + selectedLength, referenceIndex + referenceLength]
                            + (selectedLength * 10)
                            - Math.Abs(selectedLength - referenceLength);
                        if (phraseScore > bestScore)
                        {
                            bestScore = phraseScore;
                        }
                    }
                }

                score[selectedIndex, referenceIndex] = bestScore;
            }
        }

        var matches = new List<MatchedSpan>();
        var selectedCursor = 0;
        var referenceCursor = 0;
        while (selectedCursor < selectedEntries.Count && referenceCursor < referenceEntries.Count)
        {
            var matchedSpan = TrySelectBestSpan(selectedEntries, referenceEntries, score, selectedCursor, referenceCursor);
            if (matchedSpan is not null)
            {
                matches.Add(matchedSpan);
                selectedCursor += matchedSpan.SelectedLength;
                referenceCursor += matchedSpan.ReferenceLength;
                continue;
            }

            if (score[selectedCursor + 1, referenceCursor] >= score[selectedCursor, referenceCursor + 1])
            {
                selectedCursor++;
            }
            else
            {
                referenceCursor++;
            }
        }

        return matches;
    }

    private static MatchedSpan? TrySelectBestSpan(
        IReadOnlyList<WordEntry> selectedEntries,
        IReadOnlyList<WordEntry> referenceEntries,
        int[,] score,
        int selectedCursor,
        int referenceCursor)
    {
        MatchedSpan? bestSpan = null;
        var bestScore = int.MinValue;

        for (var selectedLength = 1; selectedLength <= MaxPhraseWords && selectedCursor + selectedLength <= selectedEntries.Count; selectedLength++)
        {
            for (var referenceLength = 1; referenceLength <= MaxPhraseWords && referenceCursor + referenceLength <= referenceEntries.Count; referenceLength++)
            {
                if (!PhraseMatches(selectedEntries, selectedCursor, selectedLength, referenceEntries, referenceCursor, referenceLength))
                {
                    continue;
                }

                var candidateScore = score[selectedCursor + selectedLength, referenceCursor + referenceLength]
                    + (selectedLength * 10)
                    - Math.Abs(selectedLength - referenceLength);
                if (candidateScore > bestScore)
                {
                    bestScore = candidateScore;
                    bestSpan = new MatchedSpan(selectedCursor, selectedLength, referenceCursor, referenceLength);
                }
            }
        }

        return bestSpan;
    }

    private static bool PhraseMatches(
        IReadOnlyList<WordEntry> selectedEntries,
        int selectedStart,
        int selectedLength,
        IReadOnlyList<WordEntry> referenceEntries,
        int referenceStart,
        int referenceLength)
    {
        var selectedPhrase = BuildPhraseKey(selectedEntries, selectedStart, selectedLength);
        var referencePhrase = BuildPhraseKey(referenceEntries, referenceStart, referenceLength);
        return string.Equals(selectedPhrase, referencePhrase, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildPhraseKey(IReadOnlyList<WordEntry> entries, int start, int length)
    {
        return string.Concat(entries.Skip(start).Take(length).Select(entry => entry.Word.Normalized));
    }

    private static void AdoptReferenceTiming(TranscriptWord targetWord, TranscriptWord referenceWord, string? referenceKind)
    {
        ApplyClampedTiming(targetWord, referenceWord.Start, referenceWord.End);
        targetWord.Confidence = referenceWord.Confidence ?? targetWord.Confidence;
        targetWord.TimingSource = string.IsNullOrWhiteSpace(referenceWord.TimingSource)
            ? targetWord.TimingSource
            : referenceWord.TimingSource;
        targetWord.AlignmentSource = string.IsNullOrWhiteSpace(referenceKind)
            ? "prototype_reference_match"
            : $"prototype_reference_match:{referenceKind}";
    }

    private static void ApplyClampedTiming(TranscriptWord targetWord, double proposedStart, double proposedEnd)
    {
        var start = Math.Max(0.0, proposedStart);
        var end = Math.Max(start + MinimumWordDurationSeconds, proposedEnd);
        targetWord.Start = start;
        targetWord.End = end;
    }

    private static List<string> BuildTranscriptWarnings(TranscriptAlignmentRequest request)
    {
        var warnings = new List<string>
        {
            "This aligned transcript was produced by the prototype monotonic word matcher, not full forced alignment.",
        };

        if (!string.IsNullOrWhiteSpace(request.ReferenceKind))
        {
            warnings.Add($"Reference transcript kind: {request.ReferenceKind}");
        }

        return warnings;
    }

    private static List<string> BuildResultWarnings(TranscriptAlignmentRequest request, int matchedWordCount, int selectedWordCount, int phraseAlignedWordCount, int interpolatedTimingWordCount)
    {
        var warnings = new List<string>
        {
            "Prototype alignment adopts timings only for words that can be matched monotonically between the selected and reference transcripts.",
        };

        if (matchedWordCount == 0)
        {
            warnings.Add("No monotonic word matches were found, so the selected transcript timings were kept unchanged.");
        }
        else if (matchedWordCount < selectedWordCount)
        {
            warnings.Add("Some selected words could not be matched against the reference transcript and kept their original timings.");
        }

        if (interpolatedTimingWordCount > 0)
        {
            warnings.Add($"Prototype alignment interpolated timings for {interpolatedTimingWordCount} unmatched word(s) located between matched anchors.");
        }

        if (phraseAlignedWordCount > 0)
        {
            warnings.Add($"Prototype alignment used phrase-level fallback for {phraseAlignedWordCount} selected word(s) whose tokenization differed from the reference transcript.");
        }

        if (request.Comparison is not null)
        {
            warnings.Add($"Transcript comparison F1 before alignment: {request.Comparison.Summary.F1:F4}");
        }

        return warnings;
    }

    private sealed record WordEntry(int SegmentIndex, int WordIndex, TranscriptWord Word);

    private sealed record RedistributedEntry(WordEntry WordEntry, bool IsRightAnchor, double DurationWeight);

    private sealed record MatchedSpan(int SelectedStartIndex, int SelectedLength, int ReferenceStartIndex, int ReferenceLength);
}