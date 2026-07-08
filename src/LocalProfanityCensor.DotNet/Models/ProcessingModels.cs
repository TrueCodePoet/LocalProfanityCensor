using System.Text.Json.Serialization;

namespace LocalProfanityCensor.DotNet.Models;

internal sealed class SubtitleCandidate
{
    [JsonPropertyName("source_type")]
    public string SourceType { get; set; } = "sidecar";

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("stream_index")]
    public int? StreamIndex { get; set; }

    [JsonPropertyName("codec_name")]
    public string? CodecName { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("score_hint")]
    public int ScoreHint { get; set; }
}

internal sealed class TranscriptWord
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("normalized")]
    public string Normalized { get; set; } = string.Empty;

    [JsonPropertyName("start")]
    public double Start { get; set; }

    [JsonPropertyName("end")]
    public double End { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = "unknown";

    [JsonPropertyName("timing_source")]
    public string TimingSource { get; set; } = "unknown";

    [JsonPropertyName("alignment_source")]
    public string? AlignmentSource { get; set; }
}

internal sealed class TranscriptSegment
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("start")]
    public double Start { get; set; }

    [JsonPropertyName("end")]
    public double End { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("words")]
    public List<TranscriptWord> Words { get; set; } = [];
}

internal sealed class TranscriptResult
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("candidate")]
    public SubtitleCandidate? Candidate { get; set; }

    [JsonPropertyName("segments")]
    public List<TranscriptSegment> Segments { get; set; } = [];

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];
}

internal sealed class TranscriptArtifact
{
    [JsonPropertyName("input_file")]
    public string? InputFile { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("candidate")]
    public SubtitleCandidate? Candidate { get; set; }

    [JsonPropertyName("summary")]
    public TranscriptArtifactSummary Summary { get; set; } = new();

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];

    [JsonPropertyName("segments")]
    public List<TranscriptSegment> Segments { get; set; } = [];

    [JsonPropertyName("words")]
    public List<TranscriptWord> Words { get; set; } = [];
}

internal sealed class TranscriptArtifactSummary
{
    [JsonPropertyName("segment_count")]
    public int SegmentCount { get; set; }

    [JsonPropertyName("word_count")]
    public int WordCount { get; set; }

    [JsonPropertyName("timing_sources")]
    public Dictionary<string, int> TimingSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class TranscriptComparisonResult
{
    [JsonPropertyName("comparison_method")]
    public string ComparisonMethod { get; set; } = "normalized_word_overlap";

    [JsonPropertyName("reference")]
    public TranscriptComparisonSide Reference { get; set; } = new();

    [JsonPropertyName("candidate")]
    public TranscriptComparisonSide Candidate { get; set; } = new();

    [JsonPropertyName("summary")]
    public TranscriptComparisonSummary Summary { get; set; } = new();

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];
}

internal sealed class TranscriptComparisonSide
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("input_file")]
    public string? InputFile { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("segment_count")]
    public int SegmentCount { get; set; }

    [JsonPropertyName("word_count")]
    public int WordCount { get; set; }

    [JsonPropertyName("timing_sources")]
    public Dictionary<string, int> TimingSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class TranscriptComparisonSummary
{
    [JsonPropertyName("reference_word_count")]
    public int ReferenceWordCount { get; set; }

    [JsonPropertyName("candidate_word_count")]
    public int CandidateWordCount { get; set; }

    [JsonPropertyName("matched_word_count")]
    public int MatchedWordCount { get; set; }

    [JsonPropertyName("reference_only_word_count")]
    public int ReferenceOnlyWordCount { get; set; }

    [JsonPropertyName("candidate_only_word_count")]
    public int CandidateOnlyWordCount { get; set; }

    [JsonPropertyName("precision")]
    public double Precision { get; set; }

    [JsonPropertyName("recall")]
    public double Recall { get; set; }

    [JsonPropertyName("f1")]
    public double F1 { get; set; }

    [JsonPropertyName("unique_word_jaccard")]
    public double UniqueWordJaccard { get; set; }

    [JsonPropertyName("exact_text_match")]
    public bool ExactTextMatch { get; set; }
}

internal sealed class TranscriptResolutionResult
{
    [JsonPropertyName("selected_source")]
    public string? SelectedSource { get; set; }

    [JsonPropertyName("selection_reason")]
    public string? SelectionReason { get; set; }

    [JsonPropertyName("selected_transcript")]
    public TranscriptResult? SelectedTranscript { get; set; }

    [JsonPropertyName("attempts")]
    public List<TranscriptResolutionAttempt> Attempts { get; set; } = [];

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];

    [JsonPropertyName("comparisons")]
    public List<TranscriptResolutionComparison> Comparisons { get; set; } = [];
}

internal sealed class TranscriptResolutionAttempt
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("transcript")]
    public TranscriptArtifact? Transcript { get; set; }
}

internal sealed class TranscriptResolutionComparison
{
    [JsonPropertyName("reference_kind")]
    public string ReferenceKind { get; set; } = string.Empty;

    [JsonPropertyName("candidate_kind")]
    public string CandidateKind { get; set; } = string.Empty;

    [JsonPropertyName("result")]
    public TranscriptComparisonResult Result { get; set; } = new();
}

internal sealed class TranscriptAlignmentRequest
{
    [JsonPropertyName("input_file")]
    public string? InputFile { get; set; }

    [JsonPropertyName("strategy")]
    public string Strategy { get; set; } = string.Empty;

    [JsonPropertyName("selected_kind")]
    public string SelectedKind { get; set; } = string.Empty;

    [JsonPropertyName("selected_transcript")]
    public TranscriptArtifact SelectedTranscript { get; set; } = new();

    [JsonPropertyName("reference_kind")]
    public string? ReferenceKind { get; set; }

    [JsonPropertyName("reference_transcript")]
    public TranscriptArtifact? ReferenceTranscript { get; set; }

    [JsonPropertyName("comparison")]
    public TranscriptComparisonResult? Comparison { get; set; }

    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = [];
}

internal sealed class AlignmentPrototypeResult
{
    [JsonPropertyName("input_file")]
    public string? InputFile { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("strategy")]
    public string Strategy { get; set; } = string.Empty;

    [JsonPropertyName("selected_kind")]
    public string SelectedKind { get; set; } = string.Empty;

    [JsonPropertyName("reference_kind")]
    public string? ReferenceKind { get; set; }

    [JsonPropertyName("summary")]
    public AlignmentPrototypeSummary Summary { get; set; } = new();

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];

    [JsonPropertyName("transcript")]
    public TranscriptArtifact Transcript { get; set; } = new();
}

internal sealed class AlignmentPrototypeSummary
{
    [JsonPropertyName("selected_word_count")]
    public int SelectedWordCount { get; set; }

    [JsonPropertyName("reference_word_count")]
    public int ReferenceWordCount { get; set; }

    [JsonPropertyName("matched_word_count")]
    public int MatchedWordCount { get; set; }

    [JsonPropertyName("adopted_timing_word_count")]
    public int AdoptedTimingWordCount { get; set; }

    [JsonPropertyName("phrase_aligned_word_count")]
    public int PhraseAlignedWordCount { get; set; }

    [JsonPropertyName("interpolated_timing_word_count")]
    public int InterpolatedTimingWordCount { get; set; }

    [JsonPropertyName("unmatched_selected_word_count")]
    public int UnmatchedSelectedWordCount { get; set; }
}

internal sealed class ProfanityMatch
{
    [JsonPropertyName("term_id")]
    public string TermId { get; set; } = string.Empty;

    [JsonPropertyName("matched_text")]
    public string MatchedText { get; set; } = string.Empty;

    [JsonPropertyName("normalized_text")]
    public string NormalizedText { get; set; } = string.Empty;

    [JsonPropertyName("start")]
    public double Start { get; set; }

    [JsonPropertyName("end")]
    public double End { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "medium";

    [JsonPropertyName("action")]
    public string Action { get; set; } = "beep";

    [JsonPropertyName("replacement_text")]
    public string? ReplacementText { get; set; }

    [JsonPropertyName("context_before")]
    public string ContextBefore { get; set; } = string.Empty;

    [JsonPropertyName("context_after")]
    public string ContextAfter { get; set; } = string.Empty;
}

internal sealed class CensorRange
{
    [JsonPropertyName("start")]
    public double Start { get; set; }

    [JsonPropertyName("end")]
    public double End { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = "beep";

    [JsonPropertyName("matches")]
    public List<ProfanityMatch> Matches { get; set; } = [];
}

internal sealed class ProcessResult
{
    [JsonPropertyName("input_file")]
    public string InputFile { get; set; } = string.Empty;

    [JsonPropertyName("output_file")]
    public string? OutputFile { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "failed";

    [JsonPropertyName("text_source")]
    public string? TextSource { get; set; }

    [JsonPropertyName("duration_seconds")]
    public double? DurationSeconds { get; set; }

    [JsonPropertyName("clean_audio_file")]
    public string? CleanAudioFile { get; set; }

    [JsonPropertyName("generated_subtitle_files")]
    public List<string> GeneratedSubtitleFiles { get; set; } = [];

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];

    [JsonPropertyName("matches")]
    public List<ProfanityMatch> Matches { get; set; } = [];

    [JsonPropertyName("ranges")]
    public List<CensorRange> Ranges { get; set; } = [];

    [JsonPropertyName("refinement")]
    public RefinementResult? Refinement { get; set; }

    [JsonPropertyName("work_dir")]
    public string? WorkDir { get; set; }

    [JsonPropertyName("failure_reason")]
    public string? FailureReason { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

internal sealed class ReplacePrototypeResult
{
    [JsonPropertyName("input_file")]
    public string InputFile { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "failed";

    [JsonPropertyName("work_dir")]
    public string WorkDir { get; set; } = string.Empty;

    [JsonPropertyName("selected_match_index")]
    public int SelectedMatchIndex { get; set; }

    [JsonPropertyName("selected_match")]
    public ProfanityMatch? SelectedMatch { get; set; }

    [JsonPropertyName("selected_window")]
    public RefinementWindow? SelectedWindow { get; set; }

    [JsonPropertyName("reference_clip")]
    public ReplacePrototypeClip? ReferenceClip { get; set; }

    [JsonPropertyName("target_clip")]
    public ReplacePrototypeClip? TargetClip { get; set; }

    [JsonPropertyName("subtitle_preview_files")]
    public List<string> SubtitlePreviewFiles { get; set; } = [];

    [JsonPropertyName("artifacts")]
    public ReplacePrototypeArtifacts Artifacts { get; set; } = new();

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

internal sealed class ReplacePrototypeClip
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

internal sealed class ReplacePrototypeArtifacts
{
    [JsonPropertyName("extracted_audio")]
    public string? ExtractedAudio { get; set; }

    [JsonPropertyName("vocal_stem")]
    public string? VocalStem { get; set; }

    [JsonPropertyName("background_stem")]
    public string? BackgroundStem { get; set; }

    [JsonPropertyName("vocal_target_window")]
    public string? VocalTargetWindow { get; set; }

    [JsonPropertyName("background_target_window")]
    public string? BackgroundTargetWindow { get; set; }

    [JsonPropertyName("reference_vocal_clip")]
    public string? ReferenceVocalClip { get; set; }

    [JsonPropertyName("manifest_path")]
    public string? ManifestPath { get; set; }
}

internal sealed class OpenVoicePrototypeResult
{
    [JsonPropertyName("manifest_path")]
    public string ManifestPath { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "failed";

    [JsonPropertyName("replacement_text")]
    public string ReplacementText { get; set; } = string.Empty;

    [JsonPropertyName("reference_clip")]
    public string ReferenceClip { get; set; } = string.Empty;

    [JsonPropertyName("target_clip")]
    public string TargetClip { get; set; } = string.Empty;

    [JsonPropertyName("background_clip")]
    public string BackgroundClip { get; set; } = string.Empty;

    [JsonPropertyName("generated_clip")]
    public string? GeneratedClip { get; set; }

    [JsonPropertyName("trimmed_generated_clip")]
    public string? TrimmedGeneratedClip { get; set; }

    [JsonPropertyName("fitted_generated_clip")]
    public string? FittedGeneratedClip { get; set; }

    [JsonPropertyName("replaced_vocal_clip")]
    public string? ReplacedVocalClip { get; set; }

    [JsonPropertyName("preview_mix_clip")]
    public string? PreviewMixClip { get; set; }

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

internal sealed class ReplaceSynthesisResult
{
    [JsonPropertyName("engine")]
    public string Engine { get; set; } = string.Empty;

    [JsonPropertyName("manifest_path")]
    public string ManifestPath { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "failed";

    [JsonPropertyName("replacement_text")]
    public string ReplacementText { get; set; } = string.Empty;

    [JsonPropertyName("reference_clip")]
    public string ReferenceClip { get; set; } = string.Empty;

    [JsonPropertyName("target_clip")]
    public string TargetClip { get; set; } = string.Empty;

    [JsonPropertyName("background_clip")]
    public string BackgroundClip { get; set; } = string.Empty;

    [JsonPropertyName("generated_clip")]
    public string? GeneratedClip { get; set; }

    [JsonPropertyName("trimmed_generated_clip")]
    public string? TrimmedGeneratedClip { get; set; }

    [JsonPropertyName("fitted_generated_clip")]
    public string? FittedGeneratedClip { get; set; }

    [JsonPropertyName("replaced_vocal_clip")]
    public string? ReplacedVocalClip { get; set; }

    [JsonPropertyName("preview_mix_clip")]
    public string? PreviewMixClip { get; set; }

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

internal sealed class GeneratedSubtitleTrack
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("language")]
    public string Language { get; set; } = "en";

    [JsonPropertyName("is_default")]
    public bool IsDefault { get; set; }

    [JsonPropertyName("is_censored")]
    public bool IsCensored { get; set; }
}

internal sealed class RefinementResult
{
    [JsonPropertyName("strategy")]
    public string Strategy { get; set; } = "caption-only";

    [JsonPropertyName("used_asr")]
    public bool UsedAsr { get; set; }

    [JsonPropertyName("matches")]
    public List<ProfanityMatch> Matches { get; set; } = [];

    [JsonPropertyName("windows")]
    public List<RefinementWindow> Windows { get; set; } = [];

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];
}

internal sealed class RefinementWindow
{
    [JsonPropertyName("start")]
    public double Start { get; set; }

    [JsonPropertyName("end")]
    public double End { get; set; }

    [JsonPropertyName("segments")]
    public List<TranscriptSegment> Segments { get; set; } = [];

    [JsonPropertyName("coarse_matches")]
    public List<ProfanityMatch> CoarseMatches { get; set; } = [];

    [JsonPropertyName("passes")]
    public List<RefinementPassEvidence> Passes { get; set; } = [];
}

internal sealed class RefinementPassEvidence
{
    [JsonPropertyName("engine")]
    public string Engine { get; set; } = "caption";

    [JsonPropertyName("device")]
    public string? Device { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "not_run";

    [JsonPropertyName("segments")]
    public List<TranscriptSegment> Segments { get; set; } = [];

    [JsonPropertyName("matches")]
    public List<ProfanityMatch> Matches { get; set; } = [];

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];
}