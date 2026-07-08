using LocalProfanityCensor.DotNet.Models;
using System.Text;

namespace LocalProfanityCensor.DotNet.Services;

internal static class ProfanityMatcher
{
    public static List<ProfanityMatch> DetectProfanity(List<TranscriptSegment> segments, ProfanityDictionary dictionary)
    {
        var words = FlattenWords(segments);
        var matches = new List<ProfanityMatch>();
        var index = 0;
        while (index < words.Count)
        {
            var match = MatchAtIndex(words, dictionary, index);
            if (match is null)
            {
                index++;
                continue;
            }

            matches.Add(match.Value.Match);
            index += match.Value.WordCount;
        }

        return matches;
    }

    private static List<TranscriptWord> FlattenWords(List<TranscriptSegment> segments)
    {
        return segments.SelectMany(segment => segment.Words).Where(word => !string.IsNullOrWhiteSpace(word.Normalized)).ToList();
    }

    private static (ProfanityMatch Match, int WordCount)? MatchAtIndex(List<TranscriptWord> words, ProfanityDictionary dictionary, int index)
    {
        var remaining = words.Skip(index).ToList();
        (ProfanityMatch Match, int WordCount)? bestMatch = null;

        foreach (var preparedTerm in dictionary.Terms)
        {
            foreach (var variant in preparedTerm.NormalizedVariants)
            {
                if (variant.Count > remaining.Count)
                {
                    continue;
                }

                var candidateWords = remaining.Take(variant.Count).ToList();
                if (!CandidateMatchesVariant(candidateWords, variant))
                {
                    continue;
                }

                var candidateTokens = candidateWords.Select(item => item.Normalized).ToList();
                var candidateText = string.Join(' ', candidateTokens);
                if (dictionary.Allowlist.Contains(candidateText))
                {
                    continue;
                }

                var confidenceValues = candidateWords.Where(item => item.Confidence.HasValue).Select(item => item.Confidence!.Value).ToList();
                var confidence = confidenceValues.Count > 0 ? confidenceValues.Average() : (double?)null;
                var match = new ProfanityMatch
                {
                    TermId = preparedTerm.Term.Id,
                    MatchedText = string.Join(' ', candidateWords.Select(item => item.Text)),
                    NormalizedText = candidateText,
                    Start = candidateWords[0].Start,
                    End = candidateWords[^1].End,
                    Source = candidateWords[0].Source,
                    Confidence = confidence,
                    Severity = preparedTerm.Term.Severity,
                    Action = preparedTerm.Term.Action,
                    ReplacementText = preparedTerm.Term.Replacement,
                    ContextBefore = string.Join(' ', words.Skip(Math.Max(0, index - 3)).Take(Math.Min(3, index)).Select(item => item.Text)),
                    ContextAfter = string.Join(' ', words.Skip(index + variant.Count).Take(3).Select(item => item.Text)),
                };

                if (bestMatch is null || variant.Count > bestMatch.Value.WordCount)
                {
                    bestMatch = (match, variant.Count);
                }
            }
        }

        return bestMatch;
    }

    private static bool CandidateMatchesVariant(IReadOnlyList<TranscriptWord> candidateWords, IReadOnlyList<string> variant)
    {
        if (candidateWords.Count != variant.Count)
        {
            return false;
        }

        for (var index = 0; index < variant.Count; index++)
        {
            if (!CandidateTokenMatchesVariant(candidateWords[index], variant[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CandidateTokenMatchesVariant(TranscriptWord candidateWord, string variantToken)
    {
        if (string.Equals(candidateWord.Normalized, variantToken, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (MatchesApostropheSuffix(candidateWord.Normalized, variantToken))
        {
            return true;
        }

        var maskedToken = NormalizeMaskedToken(candidateWord.Text);
        if (MatchesApostropheSuffix(maskedToken, variantToken))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(maskedToken) || !maskedToken.Contains('*') || maskedToken.Length != variantToken.Length)
        {
            return false;
        }

        for (var index = 0; index < variantToken.Length; index++)
        {
            var candidateCharacter = maskedToken[index];
            if (candidateCharacter != '*' && candidateCharacter != variantToken[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesApostropheSuffix(string? candidateToken, string variantToken)
    {
        if (string.IsNullOrWhiteSpace(candidateToken) || string.IsNullOrWhiteSpace(variantToken))
        {
            return false;
        }

        if (!candidateToken.StartsWith(variantToken, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (candidateToken.Length <= variantToken.Length || candidateToken[variantToken.Length] != '\'')
        {
            return false;
        }

        var suffix = candidateToken[(variantToken.Length + 1)..];
        return suffix.Length > 0 && suffix.All(static character => character is >= 'a' and <= 'z');
    }

    private static string NormalizeMaskedToken(string text)
    {
        var normalized = TextNormalization.NormalizeText(text);
        var builder = new StringBuilder(normalized.Length);

        foreach (var character in normalized)
        {
            if ((character >= 'a' && character <= 'z') || (character >= '0' && character <= '9') || character == '\'' || character == '*')
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Trim('\'');
    }
}