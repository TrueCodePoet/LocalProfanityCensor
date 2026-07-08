using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalProfanityCensor.DotNet.Services;

internal static partial class TextNormalization
{
    private static readonly Dictionary<char, char> LeetTable = new()
    {
        ['@'] = 'a',
        ['$'] = 's',
        ['0'] = 'o',
        ['1'] = 'i',
        ['3'] = 'e',
        ['4'] = 'a',
        ['5'] = 's',
        ['7'] = 't',
    };

    public static string NormalizeText(string text, bool preserveCase = false)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC);
        normalized = preserveCase ? normalized : normalized.ToLower(CultureInfo.InvariantCulture);

        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            builder.Append(LeetTable.TryGetValue(character, out var replacement) ? replacement : character);
        }

        normalized = RepeatRegex().Replace(builder.ToString(), "$1$1");
        normalized = MultiSpaceRegex().Replace(normalized, " ");
        return normalized.Trim();
    }

    public static string NormalizeToken(string text)
    {
        var normalized = NormalizeText(text);
        normalized = NonTokenRegex().Replace(normalized, string.Empty);
        return normalized.Trim('\'');
    }

    public static List<string> NormalizePhrase(string text)
    {
        var normalized = NormalizeText(text);
        var tokens = WhitespaceRegex().Matches(normalized)
            .Select(match => NormalizeToken(match.Value))
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToList();
        return tokens;
    }

    [GeneratedRegex("(.)\\1{2,}")]
    private static partial Regex RepeatRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex("[^a-z0-9']+")]
    private static partial Regex NonTokenRegex();

    [GeneratedRegex("\\S+")]
    private static partial Regex WhitespaceRegex();
}