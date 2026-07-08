using System.Text.Json.Serialization;

namespace LocalProfanityCensor.DotNet.Models;

internal sealed class DictionaryFile
{
    [JsonPropertyName("terms")]
    public List<DictionaryTerm> Terms { get; set; } = [];

    [JsonPropertyName("allowlist")]
    public List<string> Allowlist { get; set; } = [];
}

internal sealed class DictionaryTerm
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "medium";

    [JsonPropertyName("variants")]
    public List<string> Variants { get; set; } = [];

    [JsonPropertyName("action")]
    public string Action { get; set; } = "beep";

    [JsonPropertyName("replacement")]
    public string? Replacement { get; set; }
}

internal sealed class DictionaryValidationResult
{
    public bool IsValid { get; init; }

    public string Message { get; init; } = string.Empty;
}

internal sealed class PreparedTerm
{
    public DictionaryTerm Term { get; init; } = new();

    public List<List<string>> NormalizedVariants { get; init; } = [];
}

internal sealed class ProfanityDictionary
{
    public List<PreparedTerm> Terms { get; init; } = [];

    public HashSet<string> Allowlist { get; init; } = new(StringComparer.Ordinal);
}