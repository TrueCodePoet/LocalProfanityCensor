using System.Text.Json;
using LocalProfanityCensor.DotNet.Models;

namespace LocalProfanityCensor.DotNet.Services;

internal static class AlignmentRequestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = true,
    };

    public static TranscriptAlignmentRequest? Build(string? inputFile, TranscriptResolutionResult? transcriptResolution)
    {
        if (transcriptResolution?.SelectedTranscript is null)
        {
            return null;
        }

        var selectedAttempt = transcriptResolution.Attempts.FirstOrDefault(attempt => string.Equals(attempt.Status, "selected", StringComparison.OrdinalIgnoreCase));
        var selectedKind = selectedAttempt?.Kind ?? transcriptResolution.SelectedSource ?? "unknown";
        var selectedArtifact = selectedAttempt?.Transcript ?? TranscriptArtifactService.Build(inputFile, transcriptResolution.SelectedTranscript);

        var referenceAttempt = transcriptResolution.Attempts.FirstOrDefault(attempt =>
            !string.Equals(attempt.Kind, selectedKind, StringComparison.OrdinalIgnoreCase)
            && attempt.Transcript is not null
            && (string.Equals(attempt.Status, "reference_collected", StringComparison.OrdinalIgnoreCase)
                || string.Equals(attempt.Status, "selected", StringComparison.OrdinalIgnoreCase)));

        var comparison = transcriptResolution.Comparisons.FirstOrDefault(comparisonItem =>
            string.Equals(comparisonItem.ReferenceKind, selectedKind, StringComparison.OrdinalIgnoreCase)
            || string.Equals(comparisonItem.CandidateKind, selectedKind, StringComparison.OrdinalIgnoreCase))?.Result;

        var request = new TranscriptAlignmentRequest
        {
            InputFile = inputFile,
            Strategy = referenceAttempt is null ? "selected_transcript_to_audio" : BuildPairStrategy(selectedKind, referenceAttempt.Kind),
            SelectedKind = selectedKind,
            SelectedTranscript = selectedArtifact,
            ReferenceKind = referenceAttempt?.Kind,
            ReferenceTranscript = referenceAttempt?.Transcript,
            Comparison = comparison,
        };

        request.Notes.Add("This artifact is a planning handoff for future word alignment, not a completed alignment result.");
        request.Notes.Add(referenceAttempt is null
            ? "No secondary transcript reference was available, so alignment should operate on the selected transcript against audio only."
            : "A secondary transcript reference is available, so alignment can compare subtitle text and ASR text before choosing final word timings.");

        return request;
    }

    public static async Task<TranscriptAlignmentRequest> LoadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var request = await JsonSerializer.DeserializeAsync<TranscriptAlignmentRequest>(stream, JsonOptions);
        return request ?? throw new InvalidOperationException($"Alignment request '{path}' is empty or invalid.");
    }

    public static async Task WriteAsync(string path, TranscriptAlignmentRequest request)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory());
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(request, JsonOptions));
    }

    private static string BuildPairStrategy(string selectedKind, string referenceKind)
    {
        if (string.Equals(selectedKind, "subtitle", StringComparison.OrdinalIgnoreCase)
            && string.Equals(referenceKind, "full_audio_asr", StringComparison.OrdinalIgnoreCase))
        {
            return "subtitle_text_plus_asr_reference_to_audio";
        }

        if (string.Equals(selectedKind, "full_audio_asr", StringComparison.OrdinalIgnoreCase)
            && string.Equals(referenceKind, "subtitle", StringComparison.OrdinalIgnoreCase))
        {
            return "asr_text_plus_subtitle_reference_to_audio";
        }

        return "multi_transcript_to_audio";
    }
}