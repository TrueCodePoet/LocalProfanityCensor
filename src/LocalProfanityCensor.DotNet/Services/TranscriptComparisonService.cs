using System.Text;
using LocalProfanityCensor.DotNet.Models;

namespace LocalProfanityCensor.DotNet.Services;

internal static class TranscriptComparisonService
{
    public static TranscriptComparisonResult Compare(string? referencePath, TranscriptArtifact reference, string? candidatePath, TranscriptArtifact candidate)
    {
        var referenceWords = ExtractWords(reference);
        var candidateWords = ExtractWords(candidate);
        var matchedWordCount = CountOverlap(referenceWords, candidateWords);
        var referenceOnlyWordCount = Math.Max(0, referenceWords.Count - matchedWordCount);
        var candidateOnlyWordCount = Math.Max(0, candidateWords.Count - matchedWordCount);
        var precision = candidateWords.Count == 0 ? 0.0 : (double)matchedWordCount / candidateWords.Count;
        var recall = referenceWords.Count == 0 ? 0.0 : (double)matchedWordCount / referenceWords.Count;
        var f1 = precision + recall == 0.0 ? 0.0 : (2.0 * precision * recall) / (precision + recall);
        var referenceUnique = referenceWords.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidateUnique = candidateWords.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unionCount = referenceUnique.Union(candidateUnique, StringComparer.OrdinalIgnoreCase).Count();
        var intersectionCount = referenceUnique.Intersect(candidateUnique, StringComparer.OrdinalIgnoreCase).Count();
        var exactTextMatch = string.Equals(BuildNormalizedText(referenceWords), BuildNormalizedText(candidateWords), StringComparison.Ordinal);

        var result = new TranscriptComparisonResult
        {
            Reference = BuildSide(referencePath, reference, referenceWords.Count),
            Candidate = BuildSide(candidatePath, candidate, candidateWords.Count),
            Summary = new TranscriptComparisonSummary
            {
                ReferenceWordCount = referenceWords.Count,
                CandidateWordCount = candidateWords.Count,
                MatchedWordCount = matchedWordCount,
                ReferenceOnlyWordCount = referenceOnlyWordCount,
                CandidateOnlyWordCount = candidateOnlyWordCount,
                Precision = Math.Round(precision, 4),
                Recall = Math.Round(recall, 4),
                F1 = Math.Round(f1, 4),
                UniqueWordJaccard = unionCount == 0 ? 1.0 : Math.Round((double)intersectionCount / unionCount, 4),
                ExactTextMatch = exactTextMatch,
            },
        };

        if (referenceWords.Count == 0)
        {
            result.Warnings.Add("Reference transcript contains no comparable normalized words.");
        }

        if (candidateWords.Count == 0)
        {
            result.Warnings.Add("Candidate transcript contains no comparable normalized words.");
        }

        return result;
    }

    private static TranscriptComparisonSide BuildSide(string? path, TranscriptArtifact artifact, int wordCount)
    {
        return new TranscriptComparisonSide
        {
            Path = path,
            InputFile = artifact.InputFile,
            Source = artifact.Source,
            SegmentCount = artifact.Summary.SegmentCount,
            WordCount = wordCount,
            TimingSources = new Dictionary<string, int>(artifact.Summary.TimingSources, StringComparer.OrdinalIgnoreCase),
        };
    }

    private static List<string> ExtractWords(TranscriptArtifact artifact)
    {
        if (artifact.Words.Count > 0)
        {
            return artifact.Words
                .Select(word => !string.IsNullOrWhiteSpace(word.Normalized) ? word.Normalized : NormalizeToken(word.Text))
                .Where(static word => !string.IsNullOrWhiteSpace(word))
                .ToList();
        }

        return artifact.Segments
            .SelectMany(segment => SplitWords(segment.Text))
            .ToList();
    }

    private static int CountOverlap(IReadOnlyCollection<string> referenceWords, IReadOnlyCollection<string> candidateWords)
    {
        var referenceCounts = BuildCounts(referenceWords);
        var candidateCounts = BuildCounts(candidateWords);
        var overlap = 0;

        foreach (var (word, referenceCount) in referenceCounts)
        {
            if (candidateCounts.TryGetValue(word, out var candidateCount))
            {
                overlap += Math.Min(referenceCount, candidateCount);
            }
        }

        return overlap;
    }

    private static Dictionary<string, int> BuildCounts(IEnumerable<string> words)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in words)
        {
            counts[word] = counts.TryGetValue(word, out var count) ? count + 1 : 1;
        }

        return counts;
    }

    private static IEnumerable<string> SplitWords(string text)
    {
        var builder = new StringBuilder();
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character) || character == '\'')
            {
                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static string NormalizeToken(string value)
    {
        return string.Join(' ', SplitWords(value));
    }

    private static string BuildNormalizedText(IEnumerable<string> words)
    {
        return string.Join(' ', words);
    }
}