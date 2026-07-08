using System.Globalization;
using LocalProfanityCensor.DotNet.Models;

namespace LocalProfanityCensor.DotNet.Services;

internal static class MediaRenderService
{
    private sealed record AudioProbeInfo(int SampleRate, int Channels, string ChannelLayout);

    private static bool RequiresCompressedAudioForContainer(string outputMedia)
    {
        var extension = Path.GetExtension(outputMedia).ToLowerInvariant();
        return extension is ".mp4" or ".m4v" or ".mov";
    }

    public static async Task ExtractAudioAsync(string inputPath, string outputPath)
    {
        ToolRunner.EnsureToolExists("ffmpeg");
        await ToolRunner.RunAsync(
            "ffmpeg",
            "-y",
            "-i",
            inputPath,
            "-vn",
            "-map",
            "0:a:0",
            "-c:a",
            "pcm_s16le",
            outputPath);
    }

    public static async Task ExtractAudioSegmentAsync(string inputPath, string outputPath, double startSeconds, double endSeconds)
    {
        ToolRunner.EnsureToolExists("ffmpeg");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? Directory.GetCurrentDirectory());
        var safeStart = Math.Max(0.0, startSeconds);
        var safeEnd = Math.Max(safeStart + 0.05, endSeconds);

        await ToolRunner.RunAsync(
            "ffmpeg",
            "-y",
            "-i",
            inputPath,
            "-ss",
            safeStart.ToString("F3", CultureInfo.InvariantCulture),
            "-to",
            safeEnd.ToString("F3", CultureInfo.InvariantCulture),
            "-c:a",
            "pcm_s16le",
            outputPath);
    }

    public static async Task RenderMutedAudioAsync(string inputAudio, string outputAudio, List<CensorRange> ranges, AppConfig config)
    {
        ToolRunner.EnsureToolExists("ffmpeg");
        var filters = new List<string>();
        var currentLabel = "0:a";
        var muteRatio = config.Censor.MuteVolumeDb <= -90
            ? 0.0
            : Math.Pow(10, config.Censor.MuteVolumeDb / 20.0);

        for (var index = 0; index < ranges.Count; index++)
        {
            var censorRange = ranges[index];
            var targetRatio = string.Equals(censorRange.Action, "duck", StringComparison.OrdinalIgnoreCase)
                ? config.Censor.DuckVolumeRatio
                : muteRatio;
            var nextLabel = $"a{index}";
            filters.Add($"[{currentLabel}]volume=enable='between(t,{FormatTime(censorRange.Start)},{FormatTime(censorRange.End)})':volume={targetRatio.ToString(CultureInfo.InvariantCulture)}[{nextLabel}]");
            currentLabel = nextLabel;
        }

        if (filters.Count == 0)
        {
            await ToolRunner.RunAsync("ffmpeg", "-y", "-i", inputAudio, "-c:a", "pcm_s16le", outputAudio);
            return;
        }

        await ToolRunner.RunAsync(
            "ffmpeg",
            "-y",
            "-i",
            inputAudio,
            "-filter_complex",
            string.Join(';', filters),
            "-map",
            $"[{currentLabel}]",
            outputAudio);
    }

    public static async Task RenderBeepTrackAsync(string outputAudio, double durationSeconds, List<CensorRange> ranges, AppConfig config, MediaInfo mediaInfo)
    {
        ToolRunner.EnsureToolExists("ffmpeg");
        var beepRanges = ranges.Where(item => string.Equals(item.Action, "beep", StringComparison.OrdinalIgnoreCase)).ToList();
        var firstAudio = mediaInfo.AudioStreams.FirstOrDefault();
        var sampleRate = firstAudio?.SampleRate ?? 48000;
        var channelLayout = string.IsNullOrWhiteSpace(firstAudio?.ChannelLayout)
            ? ((firstAudio?.Channels ?? 2) > 2 ? $"{firstAudio?.Channels}c" : "stereo")
            : firstAudio!.ChannelLayout!;
        if (beepRanges.Count == 0)
        {
            await ToolRunner.RunAsync(
                "ffmpeg",
                "-y",
                "-f",
                "lavfi",
                "-i",
                $"anullsrc=r={sampleRate}:cl={channelLayout}",
                "-t",
                durationSeconds.ToString("F3", CultureInfo.InvariantCulture),
                "-c:a",
                "pcm_s16le",
                outputAudio);
            return;
        }

        var volumeRatio = Math.Pow(10, config.Censor.BeepVolumeDb / 20.0);
        var volumeExpression = BuildBeepVolumeExpression(beepRanges, volumeRatio);
        var filterScriptPath = Path.ChangeExtension(outputAudio, ".beep_filter.txt");
        await File.WriteAllTextAsync(filterScriptPath, $"[0:a]aformat=channel_layouts={channelLayout},volume='{volumeExpression}':eval=frame[aout]");
        try
        {
            await ToolRunner.RunAsync(
                "ffmpeg",
                "-y",
                "-f",
                "lavfi",
                "-i",
                $"sine=frequency={config.Censor.BeepFrequencyHz}:sample_rate={sampleRate}:duration={durationSeconds.ToString("F3", CultureInfo.InvariantCulture)}",
                "-af",
                $"aformat=channel_layouts={channelLayout}",
                "-filter_complex_script",
                filterScriptPath,
                "-map",
                "[aout]",
                "-c:a",
                "pcm_s16le",
                outputAudio);
        }
        finally
        {
            if (File.Exists(filterScriptPath))
            {
                File.Delete(filterScriptPath);
            }
        }
    }

    public static async Task MixAudioTracksAsync(string primaryAudio, string overlayAudio, string outputAudio)
    {
        ToolRunner.EnsureToolExists("ffmpeg");
        var primaryInfo = await ProbeAudioAsync(primaryAudio);
        var overlayInfo = await ProbeAudioAsync(overlayAudio);
        var outputChannels = Math.Max(primaryInfo.Channels, overlayInfo.Channels);
        var outputLayout = ResolveChannelLayout(outputChannels, primaryInfo.ChannelLayout, overlayInfo.ChannelLayout);
        var outputSampleRate = Math.Max(primaryInfo.SampleRate, overlayInfo.SampleRate);

        var filter = string.Join(';',
            $"[0:a]aresample={outputSampleRate},aformat=sample_rates={outputSampleRate}:channel_layouts={outputLayout}[a0]",
            $"[1:a]aresample={outputSampleRate},aformat=sample_rates={outputSampleRate}:channel_layouts={outputLayout}[a1]",
            "[a0][a1]amix=inputs=2:duration=first:dropout_transition=0,volume=2,alimiter=limit=0.98[aout]");

        await ToolRunner.RunAsync(
            "ffmpeg",
            "-y",
            "-i",
            primaryAudio,
            "-i",
            overlayAudio,
            "-filter_complex",
            filter,
            "-map",
            "[aout]",
            "-c:a",
            "pcm_s16le",
            outputAudio);
    }

    public static async Task RenderMutedAudioFromStemAsync(string vocalStemAudio, string outputAudio, List<CensorRange> ranges, AppConfig config)
    {
        await RenderMutedAudioAsync(vocalStemAudio, outputAudio, ranges, config);
    }

    public static async Task RenderBeepTrackForStemAsync(string outputAudio, double durationSeconds, List<CensorRange> ranges, AppConfig config, string vocalStemAudio)
    {
        ToolRunner.EnsureToolExists("ffprobe");
        var probe = await ToolRunner.RunCaptureAsync(
            "ffprobe",
            "-v",
            "error",
            "-select_streams",
            "a:0",
            "-show_entries",
            "stream=sample_rate,channel_layout,channels",
            "-of",
            "default=noprint_wrappers=1",
            vocalStemAudio);

        int? sampleRate = null;
        int? channels = null;
        string? channelLayout = null;
        foreach (var line in probe.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            switch (parts[0])
            {
                case "sample_rate" when int.TryParse(parts[1], out var parsedSampleRate):
                    sampleRate = parsedSampleRate;
                    break;
                case "channels" when int.TryParse(parts[1], out var parsedChannels):
                    channels = parsedChannels;
                    break;
                case "channel_layout":
                    channelLayout = parts[1];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(channelLayout) || string.Equals(channelLayout, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            channelLayout = channels switch
            {
                null or <= 1 => "mono",
                2 => "stereo",
                6 => "5.1",
                8 => "7.1",
                _ => $"{channels}c",
            };
        }

        var mediaInfo = new MediaInfo
        {
            AudioStreams =
            [
                new AudioStreamInfo
                {
                    SampleRate = sampleRate,
                    Channels = channels,
                    ChannelLayout = channelLayout,
                },
            ],
        };

        await RenderBeepTrackAsync(outputAudio, durationSeconds, ranges, config, mediaInfo);
    }

    public static async Task EncodeCleanAudioAsync(string inputAudio, string outputAudio, MediaInfo mediaInfo, double gainDb = 0.0)
    {
        ToolRunner.EnsureToolExists("ffmpeg");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputAudio)) ?? Directory.GetCurrentDirectory());
        var suffix = Path.GetExtension(outputAudio).ToLowerInvariant();
        var codecArgs = suffix switch
        {
            ".wav" => new[] { "-c:a", "pcm_s16le" },
            ".flac" => new[] { "-c:a", "flac" },
            ".mp3" => new[] { "-c:a", "libmp3lame", "-b:a", "192k" },
            _ => new[] { "-c:a", "aac", "-b:a", "192k" },
        };

        var channelArgs = new List<string>();
        if (mediaInfo.AudioStreams.Count > 0)
        {
            var firstAudio = mediaInfo.AudioStreams[0];
            if (firstAudio.Channels.HasValue)
            {
                channelArgs.AddRange(["-ac", firstAudio.Channels.Value.ToString(CultureInfo.InvariantCulture)]);
            }

            if (!string.IsNullOrWhiteSpace(firstAudio.ChannelLayout))
            {
                channelArgs.AddRange(["-channel_layout", firstAudio.ChannelLayout]);
            }

            if (firstAudio.SampleRate.HasValue)
            {
                channelArgs.AddRange(["-ar", firstAudio.SampleRate.Value.ToString(CultureInfo.InvariantCulture)]);
            }
        }

        var args = new List<string>
        {
            "-y",
            "-i",
            inputAudio,
        };

        if (Math.Abs(gainDb) > 0.001)
        {
            args.AddRange(
            [
                "-af",
                $"volume={gainDb.ToString("0.###", CultureInfo.InvariantCulture)}dB,alimiter=limit=0.98",
            ]);
        }

        args.AddRange(channelArgs);
        args.AddRange(codecArgs);
        args.Add(outputAudio);
        await ToolRunner.RunAsync("ffmpeg", args.ToArray());
    }

    public static async Task RemuxOutputAsync(
        string inputMedia,
        string? cleanAudio,
        string outputMedia,
        MediaInfo mediaInfo,
        IReadOnlyList<GeneratedSubtitleTrack> generatedSubtitleTracks,
        AppConfig config)
    {
        ToolRunner.EnsureToolExists("ffmpeg");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputMedia)) ?? Directory.GetCurrentDirectory());

        if (mediaInfo.VideoStreams.Count > 0)
        {
            var retainedSubtitles = GetRetainedSubtitleStreams(mediaInfo);
            var args = new List<string>
            {
                "-y",
                "-i",
                inputMedia,
            };

            if (!string.IsNullOrWhiteSpace(cleanAudio))
            {
                var firstAudioStartTime = mediaInfo.AudioStreams.FirstOrDefault()?.StartTimeSeconds ?? 0.0;
                if (firstAudioStartTime > 0.001)
                {
                    args.InsertRange(3, ["-itsoffset", FormatTime(firstAudioStartTime), "-i", cleanAudio]);
                }
                else
                {
                    args.InsertRange(3, ["-i", cleanAudio]);
                }
            }

            foreach (var track in generatedSubtitleTracks)
            {
                args.AddRange(["-i", track.Path]);
            }

            args.AddRange([
                "-map_metadata",
                "0",
                "-map",
                "0:v?",
                "-map",
                "0:a?",
                "-c:v",
                "copy",
                "-c:a",
                "copy",
                "-c:s",
                "copy",
            ]);

            if (!string.IsNullOrWhiteSpace(cleanAudio))
            {
                args.AddRange(["-map", "1:a:0"]);
            }

            if (!string.IsNullOrWhiteSpace(cleanAudio) && RequiresCompressedAudioForContainer(outputMedia))
            {
                var cleanAudioIndex = mediaInfo.AudioStreams.Count;
                args.AddRange(["-c:a:" + cleanAudioIndex.ToString(CultureInfo.InvariantCulture), "aac"]);
                args.AddRange(["-b:a:" + cleanAudioIndex.ToString(CultureInfo.InvariantCulture), "192k"]);
            }

            foreach (var (subtitleOrdinal, _) in retainedSubtitles)
            {
                args.AddRange(["-map", $"0:s:{subtitleOrdinal}"]);
            }

            var generatedSubtitleInputStartIndex = string.IsNullOrWhiteSpace(cleanAudio) ? 1 : 2;
            for (var index = 0; index < generatedSubtitleTracks.Count; index++)
            {
                args.AddRange(["-map", $"{generatedSubtitleInputStartIndex + index}:0"]);
            }

            var defaultOriginalAudioIndex = ResolveDefaultOriginalAudioIndex(mediaInfo, config, cleanAudio);
            for (var index = 0; index < mediaInfo.AudioStreams.Count; index++)
            {
                args.AddRange([
                    "-disposition:a:" + index.ToString(CultureInfo.InvariantCulture),
                    index == defaultOriginalAudioIndex ? "default" : "0"]);
            }

            if (!string.IsNullOrWhiteSpace(cleanAudio))
            {
                var cleanAudioIndex = mediaInfo.AudioStreams.Count;
                if (config.Processing.DefaultCensoredAudioTrack)
                {
                    for (var index = 0; index < mediaInfo.AudioStreams.Count; index++)
                    {
                        args.AddRange([
                            "-disposition:a:" + index.ToString(CultureInfo.InvariantCulture),
                            "0"]);
                    }
                }

                args.AddRange([
                    "-disposition:a:" + cleanAudioIndex.ToString(CultureInfo.InvariantCulture),
                    config.Processing.DefaultCensoredAudioTrack ? "default" : "0"]);
                args.AddRange(["-metadata:s:a:" + cleanAudioIndex.ToString(CultureInfo.InvariantCulture), "title=Clean Censored"]);
                if (mediaInfo.AudioStreams.Count > 0 && !string.IsNullOrWhiteSpace(mediaInfo.AudioStreams[0].Language))
                {
                    args.AddRange(["-metadata:s:a:" + cleanAudioIndex.ToString(CultureInfo.InvariantCulture), $"language={mediaInfo.AudioStreams[0].Language}"]);
                }
            }

            var defaultOriginalSubtitleIndex = ResolveDefaultOriginalSubtitleIndex(retainedSubtitles, config, generatedSubtitleTracks);
            for (var index = 0; index < retainedSubtitles.Count; index++)
            {
                args.AddRange([
                    "-disposition:s:" + index.ToString(CultureInfo.InvariantCulture),
                    index == defaultOriginalSubtitleIndex ? "default" : "0"]);
            }

            for (var index = 0; index < generatedSubtitleTracks.Count; index++)
            {
                var subtitleOutputIndex = retainedSubtitles.Count + index;
                var track = generatedSubtitleTracks[index];
                args.AddRange([
                    "-disposition:s:" + subtitleOutputIndex.ToString(CultureInfo.InvariantCulture),
                    track.IsDefault ? "default" : "0"]);
                args.AddRange(["-metadata:s:s:" + subtitleOutputIndex.ToString(CultureInfo.InvariantCulture), $"title={track.Title}"]);
                if (!string.IsNullOrWhiteSpace(track.Language))
                {
                    args.AddRange(["-metadata:s:s:" + subtitleOutputIndex.ToString(CultureInfo.InvariantCulture), $"language={track.Language}"]);
                }
            }

            args.Add(outputMedia);
            await ToolRunner.RunAsync("ffmpeg", args.ToArray());
            return;
        }

        var audioCodec = Path.GetExtension(outputMedia).ToLowerInvariant() switch
        {
            ".mp3" => "libmp3lame",
            ".wav" => "pcm_s16le",
            _ => "aac",
        };

        if (string.IsNullOrWhiteSpace(cleanAudio))
        {
            throw new InvalidOperationException("Clean audio path is required when remuxing audio-only output.");
        }

        await ToolRunner.RunAsync(
            "ffmpeg",
            "-y",
            "-i",
            cleanAudio,
            "-map",
            "0:a:0",
            "-c:a",
            audioCodec,
            outputMedia);
    }

    public static async Task CopyMediaWithoutChangesAsync(string inputMedia, string outputMedia)
    {
        ToolRunner.EnsureToolExists("ffmpeg");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputMedia)) ?? Directory.GetCurrentDirectory());
        await ToolRunner.RunAsync("ffmpeg", "-y", "-i", inputMedia, "-map", "0", "-c", "copy", outputMedia);
    }

    private static string FormatTime(double value)
    {
        return value.ToString("F3", CultureInfo.InvariantCulture);
    }

    private static string BuildBeepVolumeExpression(List<CensorRange> beepRanges, double volumeRatio)
    {
        var windows = beepRanges.Select(item => $"between(t,{FormatTime(item.Start)},{FormatTime(item.End)})").ToList();
        if (windows.Count == 0)
        {
            return "0";
        }

        var activeExpression = string.Join('+', windows);
        return $"if(gt({activeExpression},0),{volumeRatio.ToString(CultureInfo.InvariantCulture)},0)";
    }

    private static async Task<AudioProbeInfo> ProbeAudioAsync(string inputPath)
    {
        ToolRunner.EnsureToolExists("ffprobe");
        var probe = await ToolRunner.RunCaptureAsync(
            "ffprobe",
            "-v",
            "error",
            "-select_streams",
            "a:0",
            "-show_entries",
            "stream=sample_rate,channel_layout,channels",
            "-of",
            "default=noprint_wrappers=1",
            inputPath);

        int? sampleRate = null;
        int? channels = null;
        string? channelLayout = null;
        foreach (var line in probe.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            switch (parts[0])
            {
                case "sample_rate" when int.TryParse(parts[1], out var parsedSampleRate):
                    sampleRate = parsedSampleRate;
                    break;
                case "channels" when int.TryParse(parts[1], out var parsedChannels):
                    channels = parsedChannels;
                    break;
                case "channel_layout":
                    channelLayout = parts[1];
                    break;
            }
        }

        var resolvedChannels = channels ?? 2;
        return new AudioProbeInfo(
            sampleRate ?? 48000,
            resolvedChannels,
            ResolveChannelLayout(resolvedChannels, channelLayout));
    }

    private static string ResolveChannelLayout(int channels, params string?[] layouts)
    {
        foreach (var layout in layouts)
        {
            if (!string.IsNullOrWhiteSpace(layout) && !string.Equals(layout, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                return layout;
            }
        }

        return channels switch
        {
            <= 1 => "mono",
            2 => "stereo",
            6 => "5.1",
            8 => "7.1",
            _ => $"{channels}c",
        };
    }

    internal static List<(int SubtitleOrdinal, SubtitleStreamInfo Stream)> GetRetainedSubtitleStreams(MediaInfo mediaInfo)
    {
        var englishFullLengthExists = mediaInfo.SubtitleStreams.Any(stream =>
            new[] { "en", "eng" }.Contains((stream.Language ?? string.Empty).ToLowerInvariant())
            && (stream.DurationSeconds ?? 0.0) >= 180.0
            && !(stream.Title ?? string.Empty).Contains("forced", StringComparison.OrdinalIgnoreCase));

        var retained = new List<(int SubtitleOrdinal, SubtitleStreamInfo Stream)>();
        for (var index = 0; index < mediaInfo.SubtitleStreams.Count; index++)
        {
            var stream = mediaInfo.SubtitleStreams[index];
            var title = (stream.Title ?? string.Empty).ToLowerInvariant();
            if (title.Contains("forced", StringComparison.Ordinal)
                && (stream.DurationSeconds ?? 0.0) < 180.0
                && englishFullLengthExists)
            {
                continue;
            }

            retained.Add((index, stream));
        }

        return retained;
    }

    private static int ResolveDefaultOriginalAudioIndex(MediaInfo mediaInfo, AppConfig config, string? cleanAudio)
    {
        if (mediaInfo.AudioStreams.Count == 0)
        {
            return -1;
        }

        if (!string.IsNullOrWhiteSpace(cleanAudio) && config.Processing.DefaultCensoredAudioTrack)
        {
            return -1;
        }

        var explicitDefaultIndex = mediaInfo.AudioStreams.FindIndex(stream => stream.IsDefault);
        return explicitDefaultIndex >= 0 ? explicitDefaultIndex : 0;
    }

    private static int ResolveDefaultOriginalSubtitleIndex(
        IReadOnlyList<(int SubtitleOrdinal, SubtitleStreamInfo Stream)> retainedSubtitles,
        AppConfig config,
        IReadOnlyList<GeneratedSubtitleTrack> generatedSubtitleTracks)
    {
        if (config.Subtitles.DefaultCensoredSubtitle && generatedSubtitleTracks.Any(track => track.IsCensored))
        {
            return -1;
        }

        if (retainedSubtitles.Count == 0)
        {
            return -1;
        }

        var explicitNormalDefaultIndex = retainedSubtitles
            .Select((item, index) => new { item.Stream.IsDefault, item.Stream.IsForced, Index = index })
            .FirstOrDefault(item => item.IsDefault && !item.IsForced)?.Index;
        if (explicitNormalDefaultIndex.HasValue)
        {
            return explicitNormalDefaultIndex.Value;
        }

        var firstNormalIndex = retainedSubtitles
            .Select((item, index) => new { item.Stream.IsForced, Index = index })
            .FirstOrDefault(item => !item.IsForced)?.Index;
        if (firstNormalIndex.HasValue)
        {
            return firstNormalIndex.Value;
        }

        var explicitDefaultIndex = retainedSubtitles
            .Select((item, index) => new { item.Stream.IsDefault, Index = index })
            .FirstOrDefault(item => item.IsDefault)?.Index;
        return explicitDefaultIndex ?? 0;
    }
}