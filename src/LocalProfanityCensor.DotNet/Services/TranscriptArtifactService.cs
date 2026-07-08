using System.Text.Json;
using LocalProfanityCensor.DotNet.Models;

namespace LocalProfanityCensor.DotNet.Services;

internal static class TranscriptArtifactService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = true,
    };

    public static TranscriptArtifact Build(string? inputFile, TranscriptResult transcript)
    {
        var words = transcript.Segments
            .SelectMany(segment => segment.Words)
            .ToList();

        var timingSources = words
            .GroupBy(word => string.IsNullOrWhiteSpace(word.TimingSource) ? "unknown" : word.TimingSource)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return new TranscriptArtifact
        {
            InputFile = inputFile,
            Source = transcript.Source,
            Candidate = transcript.Candidate,
            Summary = new TranscriptArtifactSummary
            {
                SegmentCount = transcript.Segments.Count,
                WordCount = words.Count,
                TimingSources = timingSources,
            },
            Warnings = [.. transcript.Warnings],
            Segments = transcript.Segments,
            Words = words,
        };
    }

    public static TranscriptResult ToTranscriptResult(TranscriptArtifact artifact)
    {
        return new TranscriptResult
        {
            Source = artifact.Source,
            Candidate = artifact.Candidate,
            Segments = artifact.Segments,
            Warnings = [.. artifact.Warnings],
        };
    }

    public static async Task<TranscriptArtifact> LoadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var artifact = await JsonSerializer.DeserializeAsync<TranscriptArtifact>(stream, JsonOptions);
        return artifact ?? throw new InvalidOperationException($"Transcript artifact '{path}' is empty or invalid.");
    }

    public static async Task WriteAsync(string path, TranscriptArtifact artifact)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory());
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(artifact, JsonOptions));
    }
}