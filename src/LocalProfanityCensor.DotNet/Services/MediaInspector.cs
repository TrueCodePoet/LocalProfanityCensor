using System.Text.Json;
using LocalProfanityCensor.DotNet.Models;

namespace LocalProfanityCensor.DotNet.Services;

internal static class MediaInspector
{
    public static async Task<MediaInfo> InspectAsync(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Input media file was not found: {path}", path);
        }

        ToolRunner.EnsureToolExists("ffprobe");

        var commandResult = await ToolRunner.RunCaptureAsync(
            "ffprobe",
            "-v", "error",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            path);

        using var document = JsonDocument.Parse(commandResult.StandardOutput);
        var root = document.RootElement;
        var format = root.TryGetProperty("format", out var formatElement) ? formatElement : default;
        var streams = root.TryGetProperty("streams", out var streamElement) && streamElement.ValueKind == JsonValueKind.Array
            ? streamElement.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

        var mediaInfo = new MediaInfo
        {
            Path = path,
            FileSizeBytes = GetInt64(format, "size") ?? new FileInfo(path).Length,
            DurationSeconds = GetDouble(format, "duration") ?? 0.0,
            FormatName = GetString(format, "format_name"),
            SidecarSubtitles = DiscoverSidecarSubtitles(path),
        };

        foreach (var stream in streams)
        {
            var codecType = GetString(stream, "codec_type");
            var tags = stream.TryGetProperty("tags", out var tagsElement) ? tagsElement : default;
            var disposition = stream.TryGetProperty("disposition", out var dispositionElement) ? dispositionElement : default;
            var language = GetString(tags, "language");

            switch (codecType)
            {
                case "video":
                    mediaInfo.VideoStreams.Add(new VideoStreamInfo
                    {
                        Index = GetInt32(stream, "index") ?? 0,
                        CodecName = GetString(stream, "codec_name"),
                        Language = language,
                        Width = GetInt32(stream, "width"),
                        Height = GetInt32(stream, "height"),
                    });
                    break;
                case "audio":
                    mediaInfo.AudioStreams.Add(new AudioStreamInfo
                    {
                        Index = GetInt32(stream, "index") ?? 0,
                        CodecName = GetString(stream, "codec_name"),
                        Language = language,
                        Title = GetString(tags, "title"),
                        Channels = GetInt32(stream, "channels"),
                        ChannelLayout = GetString(stream, "channel_layout"),
                        SampleRate = ParseNullableInt(GetString(stream, "sample_rate")),
                        StartTimeSeconds = GetDouble(stream, "start_time"),
                        IsDefault = GetBool(disposition, "default"),
                    });
                    break;
                case "subtitle":
                    var durationRaw = GetString(tags, "DURATION") ?? GetString(stream, "duration");
                    mediaInfo.SubtitleStreams.Add(new SubtitleStreamInfo
                    {
                        Index = GetInt32(stream, "index") ?? 0,
                        CodecName = GetString(stream, "codec_name"),
                        Language = language,
                        Title = GetString(tags, "title"),
                        DurationSeconds = ParseStreamDurationSeconds(durationRaw),
                        IsDefault = GetBool(disposition, "default"),
                        IsForced = GetBool(disposition, "forced"),
                    });
                    break;
            }
        }

        return mediaInfo;
    }

    private static List<string> DiscoverSidecarSubtitles(string mediaPath)
    {
        var result = new List<string>();
        var directory = Path.GetDirectoryName(mediaPath) ?? Directory.GetCurrentDirectory();
        var stem = Path.GetFileNameWithoutExtension(mediaPath);
        string[] extensions = [".srt", ".vtt"];
        string[] suffixes = [string.Empty, ".en", ".eng"];

        foreach (var suffix in suffixes)
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, $"{stem}{suffix}{extension}");
                if (File.Exists(candidate))
                {
                    result.Add(candidate);
                }
            }
        }

        return result;
    }

    private static double? ParseStreamDurationSeconds(string? durationValue)
    {
        if (string.IsNullOrWhiteSpace(durationValue))
        {
            return null;
        }

        if (double.TryParse(durationValue, out var numericDuration))
        {
            return numericDuration;
        }

        var parts = durationValue.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            return null;
        }

        if (!int.TryParse(parts[0], out var hours) || !int.TryParse(parts[1], out var minutes) || !double.TryParse(parts[2], out var seconds))
        {
            return null;
        }

        return (hours * 3600) + (minutes * 60) + seconds;
    }

    private static int? ParseNullableInt(string? value)
    {
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind != JsonValueKind.Undefined && element.TryGetProperty(propertyName, out var property)
            ? property.ValueKind switch
            {
                JsonValueKind.String => property.GetString(),
                JsonValueKind.Number => property.GetRawText(),
                _ => null,
            }
            : null;
    }

    private static int? GetInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Undefined || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var numberValue))
        {
            return numberValue;
        }

        return int.TryParse(property.GetRawText().Trim('"'), out var parsed) ? parsed : null;
    }

    private static long? GetInt64(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Undefined || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var numberValue))
        {
            return numberValue;
        }

        return long.TryParse(property.GetRawText().Trim('"'), out var parsed) ? parsed : null;
    }

    private static double? GetDouble(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Undefined || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var numberValue))
        {
            return numberValue;
        }

        return double.TryParse(property.GetRawText().Trim('"'), out var parsed) ? parsed : null;
    }

    private static bool GetBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Undefined || !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => property.TryGetInt32(out var numeric) && numeric != 0,
            _ => false,
        };
    }
}