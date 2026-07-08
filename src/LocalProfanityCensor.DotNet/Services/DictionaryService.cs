using LocalProfanityCensor.DotNet.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LocalProfanityCensor.DotNet.Services;

internal static class DictionaryService
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static DictionaryValidationResult ValidateDictionary(string path)
    {
        try
        {
            var dictionary = LoadPreparedDictionary(path);
            if (dictionary.Terms.Count == 0)
            {
                return new DictionaryValidationResult
                {
                    IsValid = false,
                    Message = "Dictionary must define a non-empty terms list.",
                };
            }

            foreach (var term in dictionary.Terms)
            {
                if (string.IsNullOrWhiteSpace(term.Term.Id) || string.IsNullOrWhiteSpace(term.Term.Pattern) || string.IsNullOrWhiteSpace(term.Term.Type))
                {
                    return new DictionaryValidationResult
                    {
                        IsValid = false,
                        Message = "Each dictionary term must define id, pattern, and type.",
                    };
                }
            }

            return new DictionaryValidationResult
            {
                IsValid = true,
                Message = $"Loaded {dictionary.Terms.Count} term(s) and {dictionary.Allowlist.Count} allowlist entry(ies).",
            };
        }
        catch (Exception ex)
        {
            return new DictionaryValidationResult
            {
                IsValid = false,
                Message = ex.Message,
            };
        }
    }

    public static ProfanityDictionary LoadPreparedDictionary(string path)
    {
        var dictionary = LoadRaw(path);
        if (dictionary.Terms.Count == 0)
        {
            throw new RuntimeException("Dictionary must define a non-empty terms list.");
        }

        var preparedTerms = new List<PreparedTerm>();
        foreach (var term in dictionary.Terms)
        {
            if (string.IsNullOrWhiteSpace(term.Id) || string.IsNullOrWhiteSpace(term.Pattern) || string.IsNullOrWhiteSpace(term.Type))
            {
                throw new RuntimeException("Each dictionary term must define id, pattern, and type.");
            }

            var normalizedVariants = new List<List<string>>();
            var primary = TextNormalization.NormalizePhrase(term.Pattern);
            if (primary.Count > 0)
            {
                normalizedVariants.Add(primary);
            }

            foreach (var variant in term.Variants)
            {
                var normalizedVariant = TextNormalization.NormalizePhrase(variant);
                if (normalizedVariant.Count > 0)
                {
                    normalizedVariants.Add(normalizedVariant);
                }
            }

            preparedTerms.Add(new PreparedTerm
            {
                Term = term,
                NormalizedVariants = normalizedVariants,
            });
        }

        var allowlist = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in dictionary.Allowlist)
        {
            var normalized = TextNormalization.NormalizePhrase(item);
            if (normalized.Count > 0)
            {
                allowlist.Add(string.Join(' ', normalized));
            }
        }

        return new ProfanityDictionary
        {
            Terms = preparedTerms,
            Allowlist = allowlist,
        };
    }

    private static DictionaryFile LoadRaw(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Dictionary file was not found: {path}", path);
        }

        var yaml = File.ReadAllText(path);
        var dictionary = Deserializer.Deserialize<DictionaryFile>(yaml) ?? new DictionaryFile();
        return dictionary;
    }
}

internal sealed class RuntimeException(string message) : Exception(message);