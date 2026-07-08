using System.Text.Json;
using System.Text.Json.Serialization;
using LocalProfanityCensor.DotNet.Models;

namespace LocalProfanityCensor.DotNet.Services;

internal static class ReplaceManifestLoader
{
    public static async Task<ReplacePrototypeManifest> LoadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;

        var manifest = new ReplacePrototypeManifest
        {
            ReplacementText = root.TryGetProperty("replacement_text", out var replacementTextElement)
                ? replacementTextElement.GetString() ?? string.Empty
                : string.Empty,
            ReplacementPhraseText = root.TryGetProperty("replacement_phrase_text", out var replacementPhraseTextElement)
                ? replacementPhraseTextElement.GetString() ?? string.Empty
                : string.Empty,
            SelectedMatch = root.TryGetProperty("selected_match", out var selectedMatchElement)
                ? selectedMatchElement.Deserialize<ProfanityMatch>() ?? new ProfanityMatch()
                : new ProfanityMatch(),
            ReferenceClip = root.TryGetProperty("reference_clip", out var referenceClipElement)
                ? referenceClipElement.Deserialize<ManifestClip>() ?? new ManifestClip()
                : new ManifestClip(),
            TargetClip = root.TryGetProperty("target_clip", out var targetClipElement)
                ? targetClipElement.Deserialize<ManifestClip>() ?? new ManifestClip()
                : new ManifestClip(),
            BackgroundStemPath = root.TryGetProperty("background_stem", out var backgroundStemElement)
                ? backgroundStemElement.GetString() ?? string.Empty
                : string.Empty,
        };

        if (root.TryGetProperty("background_clip", out var backgroundClipElement))
        {
            manifest.BackgroundClip = backgroundClipElement.ValueKind switch
            {
                JsonValueKind.String => new ManifestClip { Path = backgroundClipElement.GetString() ?? string.Empty },
                JsonValueKind.Object => backgroundClipElement.Deserialize<ManifestClip>() ?? new ManifestClip(),
                _ => new ManifestClip(),
            };
        }

        return manifest;
    }

    public static string ResolveBackgroundClipPath(ReplacePrototypeManifest manifest)
    {
        if (!string.IsNullOrWhiteSpace(manifest.BackgroundClip.Path))
        {
            return manifest.BackgroundClip.Path;
        }

        var targetClipDirectory = Path.GetDirectoryName(manifest.TargetClip.Path);
        if (!string.IsNullOrWhiteSpace(targetClipDirectory))
        {
            var siblingBackgroundClip = Path.Combine(targetClipDirectory, "target.background.wav");
            if (File.Exists(siblingBackgroundClip))
            {
                return siblingBackgroundClip;
            }
        }

        return manifest.BackgroundStemPath;
    }
}

internal sealed class ReplacePrototypeManifest
{
    [JsonPropertyName("replacement_text")]
    public string ReplacementText { get; set; } = string.Empty;

    [JsonPropertyName("replacement_phrase_text")]
    public string ReplacementPhraseText { get; set; } = string.Empty;

    [JsonPropertyName("selected_match")]
    public ProfanityMatch SelectedMatch { get; set; } = new();

    [JsonPropertyName("reference_clip")]
    public ManifestClip ReferenceClip { get; set; } = new();

    [JsonPropertyName("target_clip")]
    public ManifestClip TargetClip { get; set; } = new();

    [JsonPropertyName("background_stem")]
    public string BackgroundStemPath { get; set; } = string.Empty;

    [JsonPropertyName("background_clip")]
    public ManifestClip BackgroundClip { get; set; } = new();
}

internal sealed class ManifestClip
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("start")]
    public double Start { get; set; }

    [JsonPropertyName("end")]
    public double End { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("match_start")]
    public double? MatchStart { get; set; }

    [JsonPropertyName("match_end")]
    public double? MatchEnd { get; set; }
}