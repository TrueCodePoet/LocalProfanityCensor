using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace LocalProfanityCensor.DotNet.Models;

internal sealed class AppConfig
{
    [YamlMember(Alias = "processing")]
    [JsonPropertyName("processing")]
    public ProcessingConfig Processing { get; set; } = new();

    [YamlMember(Alias = "censor")]
    [JsonPropertyName("censor")]
    public CensorConfig Censor { get; set; } = new();

    [YamlMember(Alias = "transcription")]
    [JsonPropertyName("transcription")]
    public TranscriptionConfig Transcription { get; set; } = new();

    [YamlMember(Alias = "subtitles")]
    [JsonPropertyName("subtitles")]
    public SubtitlesConfig Subtitles { get; set; } = new();

    [YamlMember(Alias = "reports")]
    [JsonPropertyName("reports")]
    public ReportsConfig Reports { get; set; } = new();

    [YamlMember(Alias = "dictionary_path")]
    [JsonPropertyName("dictionary_path")]
    public string? DictionaryPath { get; set; }

    [YamlMember(Alias = "dry_run")]
    [JsonPropertyName("dry_run")]
    public bool DryRun { get; set; }

    [YamlMember(Alias = "keep_work")]
    [JsonPropertyName("keep_work")]
    public bool KeepWork { get; set; }
}

internal sealed class ProcessingConfig
{
    [JsonPropertyName("cc_first")]
    public bool CcFirst { get; set; } = true;

    [JsonPropertyName("asr_fallback")]
    public bool AsrFallback { get; set; }

    [JsonPropertyName("collect_reference_transcripts")]
    public bool CollectReferenceTranscripts { get; set; }

    [JsonPropertyName("language")]
    public string Language { get; set; } = "en";

    [JsonPropertyName("workers")]
    public int Workers { get; set; } = 1;

    [JsonPropertyName("overwrite")]
    public bool Overwrite { get; set; }

    [JsonPropertyName("preserve_video")]
    public bool PreserveVideo { get; set; } = true;

    [JsonPropertyName("preserve_metadata")]
    public bool PreserveMetadata { get; set; } = true;

    [JsonPropertyName("default_censored_audio_track")]
    public bool DefaultCensoredAudioTrack { get; set; }
}

internal sealed class CensorConfig
{
    [YamlMember(Alias = "mode")]
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "mute";

    [YamlMember(Alias = "replace_engine")]
    [JsonPropertyName("replace_engine")]
    public string ReplaceEngine { get; set; } = "openvoice";

    [JsonPropertyName("padding_start_ms")]
    public int PaddingStartMs { get; set; } = 120;

    [JsonPropertyName("padding_end_ms")]
    public int PaddingEndMs { get; set; } = 180;

    [JsonPropertyName("merge_gap_ms")]
    public int MergeGapMs { get; set; } = 150;

    [JsonPropertyName("beep_frequency_hz")]
    public int BeepFrequencyHz { get; set; } = 1000;

    [JsonPropertyName("beep_volume_db")]
    public double BeepVolumeDb { get; set; } = -8.0;

    [JsonPropertyName("mute_volume_db")]
    public double MuteVolumeDb { get; set; } = -96.0;

    [JsonPropertyName("duck_volume_ratio")]
    public double DuckVolumeRatio { get; set; } = 0.1;
}

internal sealed class TranscriptionConfig
{
    [JsonPropertyName("engine")]
    public string Engine { get; set; } = "caption-first";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "medium";

    [JsonPropertyName("device")]
    public string Device { get; set; } = "auto";

    [JsonPropertyName("compute_type")]
    public string ComputeType { get; set; } = "auto";

    [JsonPropertyName("word_timestamps")]
    public bool WordTimestamps { get; set; } = true;

    [JsonPropertyName("vad_filter")]
    public bool VadFilter { get; set; } = true;

    [JsonPropertyName("full_audio_source_path")]
    public string? FullAudioSourcePath { get; set; }

    [JsonPropertyName("refine_window_padding_ms")]
    public int RefineWindowPaddingMs { get; set; } = 400;

    [JsonPropertyName("refine_window_lead_ms")]
    public int? RefineWindowLeadMs { get; set; }

    [JsonPropertyName("refine_window_trail_ms")]
    public int? RefineWindowTrailMs { get; set; }

    [JsonPropertyName("refine_window_max_duration_ms")]
    public int RefineWindowMaxDurationMs { get; set; } = 6000;

    [JsonPropertyName("dialog_isolation")]
    public string DialogIsolation { get; set; } = "ffmpeg-dialog";

    [JsonPropertyName("demucs_profile")]
    public string DemucsProfile { get; set; } = "depth";

    [JsonPropertyName("demucs_model")]
    public string DemucsModel { get; set; } = string.Empty;

    [JsonPropertyName("demucs_device")]
    public string DemucsDevice { get; set; } = "auto";

    [JsonPropertyName("deepfilternet_model")]
    public string DeepfilternetModel { get; set; } = "DeepFilterNet2";

    [JsonPropertyName("deepfilternet_post_filter")]
    public bool DeepfilternetPostFilter { get; set; }

    [JsonPropertyName("hard_window_fallback_engine")]
    public string HardWindowFallbackEngine { get; set; } = "none";

    [JsonPropertyName("hard_window_fallback_device")]
    public string HardWindowFallbackDevice { get; set; } = "auto";

    [JsonPropertyName("hard_window_confidence_threshold")]
    public double HardWindowConfidenceThreshold { get; set; } = 0.5;
}

internal sealed class SubtitlesConfig
{
    [YamlMember(Alias = "prefer_embedded")]
    [JsonPropertyName("prefer_embedded")]
    public bool PreferEmbedded { get; set; } = true;

    [YamlMember(Alias = "prefer_sidecar")]
    [JsonPropertyName("prefer_sidecar")]
    public bool PreferSidecar { get; set; } = true;

    [YamlMember(Alias = "allowed_languages")]
    [JsonPropertyName("allowed_languages")]
    public List<string> AllowedLanguages { get; set; } = ["en", "eng"];

    [JsonPropertyName("min_caption_count")]
    public int MinCaptionCount { get; set; } = 5;

    [JsonPropertyName("min_score")]
    public int MinScore { get; set; } = 60;

    [JsonPropertyName("timing_offset_ms")]
    public int TimingOffsetMs { get; set; }

    [YamlMember(Alias = "generate_censored_subtitle")]
    [JsonPropertyName("generate_censored_subtitle")]
    public bool GenerateCensoredSubtitle { get; set; }

    [YamlMember(Alias = "generate_plain_subtitle_if_missing")]
    [JsonPropertyName("generate_plain_subtitle_if_missing")]
    public bool GeneratePlainSubtitleIfMissing { get; set; } = true;

    [YamlMember(Alias = "default_censored_subtitle")]
    [JsonPropertyName("default_censored_subtitle")]
    public bool DefaultCensoredSubtitle { get; set; }
}

internal sealed class ReportsConfig
{
    [YamlMember(Alias = "json")]
    [JsonPropertyName("json")]
    public bool JsonEnabled { get; set; } = true;

    [YamlMember(Alias = "csv")]
    [JsonPropertyName("csv")]
    public bool CsvEnabled { get; set; } = true;

    [JsonPropertyName("include_clean_transcript")]
    public bool IncludeCleanTranscript { get; set; } = true;

    [JsonPropertyName("include_flagged_terms")]
    public bool IncludeFlaggedTerms { get; set; } = true;

    [JsonPropertyName("include_confidence")]
    public bool IncludeConfidence { get; set; } = true;
}