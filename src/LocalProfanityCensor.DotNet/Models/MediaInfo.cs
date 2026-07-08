using System.Text.Json.Serialization;

namespace LocalProfanityCensor.DotNet.Models;

internal sealed class MediaInfo
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("file_size_bytes")]
    public long FileSizeBytes { get; set; }

    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds { get; set; }

    [JsonPropertyName("format_name")]
    public string? FormatName { get; set; }

    [JsonPropertyName("video_streams")]
    public List<VideoStreamInfo> VideoStreams { get; set; } = [];

    [JsonPropertyName("audio_streams")]
    public List<AudioStreamInfo> AudioStreams { get; set; } = [];

    [JsonPropertyName("subtitle_streams")]
    public List<SubtitleStreamInfo> SubtitleStreams { get; set; } = [];

    [JsonPropertyName("sidecar_subtitles")]
    public List<string> SidecarSubtitles { get; set; } = [];
}

internal sealed class VideoStreamInfo
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("codec_name")]
    public string? CodecName { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }
}

internal sealed class AudioStreamInfo
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("codec_name")]
    public string? CodecName { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("channels")]
    public int? Channels { get; set; }

    [JsonPropertyName("channel_layout")]
    public string? ChannelLayout { get; set; }

    [JsonPropertyName("sample_rate")]
    public int? SampleRate { get; set; }

    [JsonPropertyName("start_time_seconds")]
    public double? StartTimeSeconds { get; set; }

    [JsonPropertyName("is_default")]
    public bool IsDefault { get; set; }
}

internal sealed class SubtitleStreamInfo
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("codec_name")]
    public string? CodecName { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("duration_seconds")]
    public double? DurationSeconds { get; set; }

    [JsonPropertyName("is_default")]
    public bool IsDefault { get; set; }

    [JsonPropertyName("is_forced")]
    public bool IsForced { get; set; }
}