namespace LocalProfanityCensor.DotNet.Services;

internal static class ReplaceAudioPostProcessor
{
    private const double SentenceFitToleranceSeconds = 0.12;
    private const double MaximumLoudnessGainDb = 18.0;
    private const double CosyVoiceDirectGainDb = 14.0;
    private const double ReplacementDuckVolume = 0.0;
    private const double ReplacementMuteVolume = 0.0;

    public static async Task TrimGeneratedClipAsync(string inputPath, string outputPath, string engineName)
    {
        if (string.Equals(engineName, "CosyVoice", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(inputPath, outputPath, overwrite: true);
            return;
        }

        var inputDuration = await ProbeAudioDurationAsync(inputPath);
        await ToolRunner.RunAsync(
            "ffmpeg",
            new ToolRunner.CommandProgressInfo("replace", $"Trimming generated {engineName} clip", null, Path.GetFileName(inputPath), "replace"),
            "-y",
            "-i",
            inputPath,
            "-af",
            "silenceremove=start_periods=1:start_threshold=-45dB:stop_periods=-1:stop_threshold=-45dB",
            outputPath);

        var trimmedDuration = await ProbeAudioDurationAsync(outputPath);
        if ((trimmedDuration < 0.25 || trimmedDuration < inputDuration * 0.35) && inputDuration >= 0.05)
        {
            File.Copy(inputPath, outputPath, overwrite: true);
        }
    }

    public static async Task FitGeneratedClipAsync(string inputPath, string outputPath, double targetDuration, double inputDuration, string engineName)
    {
        if (Math.Abs(inputDuration - targetDuration) <= SentenceFitToleranceSeconds)
        {
            File.Copy(inputPath, outputPath, overwrite: true);
            return;
        }

        var effectiveInputDuration = Math.Max(0.05, inputDuration);
        var tempoRatio = Math.Clamp(effectiveInputDuration / Math.Max(0.05, targetDuration), 0.25, 8.0);
        var tempoFilter = BuildAtempoFilter(tempoRatio);
        var trimEnd = targetDuration.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        var tempOutputPath = Path.Combine(
            Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory(),
            Path.GetFileNameWithoutExtension(outputPath) + ".tmp" + Path.GetExtension(outputPath));
        var retryOutputPath = Path.Combine(
            Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory(),
            Path.GetFileNameWithoutExtension(outputPath) + ".retry" + Path.GetExtension(outputPath));

        await ToolRunner.RunAsync(
            "ffmpeg",
            new ToolRunner.CommandProgressInfo("replace", $"Fitting generated {engineName} clip", null, Path.GetFileName(inputPath), "replace"),
            "-y",
            "-i",
            inputPath,
            "-af",
            $"{tempoFilter},apad=pad_dur={trimEnd},atrim=0:{trimEnd}",
            tempOutputPath);

        var fittedDuration = await ProbeAudioDurationAsync(tempOutputPath);
        if (fittedDuration < Math.Max(0.05, targetDuration - 0.05))
        {
            await ToolRunner.RunAsync(
                "ffmpeg",
                new ToolRunner.CommandProgressInfo("replace", $"Retrying generated {engineName} fit", null, Path.GetFileName(inputPath), "replace"),
                "-y",
                "-i",
                inputPath,
                "-af",
                $"{tempoFilter},apad=whole_dur={trimEnd},atrim=0:{trimEnd}",
                retryOutputPath);

            var retryDuration = await ProbeAudioDurationAsync(retryOutputPath);
            if (retryDuration < Math.Max(0.05, targetDuration - 0.05))
            {
                throw new InvalidOperationException($"Generated {engineName} fit collapsed unexpectedly. target={targetDuration:F3}s input={inputDuration:F3}s first={fittedDuration:F3}s retry={retryDuration:F3}s");
            }

            File.Move(retryOutputPath, outputPath, overwrite: true);

            if (File.Exists(tempOutputPath))
            {
                File.Delete(tempOutputPath);
            }

            return;
        }

        File.Move(tempOutputPath, outputPath, overwrite: true);
    }

    public static async Task SpliceIntoTargetClipAsync(
        string targetClipPath,
        string replacementClipPath,
        string outputPath,
        double relativeWordStart,
        double relativeWordEnd,
        double targetWindowDuration,
        string engineName)
    {
        var normalizedReplacementPath = Path.Combine(
            Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory(),
            Path.GetFileNameWithoutExtension(outputPath) + ".normalized" + Path.GetExtension(outputPath));
        var mutedTargetPath = Path.Combine(
            Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory(),
            Path.GetFileNameWithoutExtension(outputPath) + ".muted-target" + Path.GetExtension(outputPath));

        await MatchLoudnessAsync(targetClipPath, replacementClipPath, normalizedReplacementPath, relativeWordStart, relativeWordEnd, engineName);

        var replacementDuration = await ProbeAudioDurationAsync(normalizedReplacementPath);
        var crossfadeDuration = Math.Min(
            0.06,
            Math.Min(
                Math.Max(0.0, relativeWordStart),
                Math.Max(0.0, Math.Min((relativeWordEnd - relativeWordStart) / 2.0, replacementDuration / 2.0))));

        var mutedStart = Math.Max(0.0, relativeWordStart - crossfadeDuration);
        var mutedEnd = Math.Min(targetWindowDuration, relativeWordEnd + crossfadeDuration);

        replacementDuration = Math.Max(0.001, replacementDuration);
        var replacementFadeDuration = Math.Max(0.001, Math.Min(crossfadeDuration, replacementDuration / 2.0));
        var replacementFadeOutStart = Math.Max(0.0, replacementDuration - replacementFadeDuration);

        var mutedStartSeconds = mutedStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        var mutedEndSeconds = mutedEnd.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        var replacementFadeDurationSeconds = replacementFadeDuration.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        var replacementFadeOutStartSeconds = replacementFadeOutStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        var targetDurationSeconds = targetWindowDuration.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        var delayMs = Math.Max(0, (int)Math.Round(relativeWordStart * 1000.0));

        await MuteTargetWindowAsync(targetClipPath, mutedTargetPath, mutedStartSeconds, mutedEndSeconds, engineName);

        var filter = string.Join(';', new[]
        {
            $"[1:a]aresample=44100,aformat=channel_layouts=stereo,afade=t=in:st=0:d={replacementFadeDurationSeconds},afade=t=out:st={replacementFadeOutStartSeconds}:d={replacementFadeDurationSeconds},adelay={delayMs}|{delayMs},apad=pad_dur={targetDurationSeconds},atrim=0:{targetDurationSeconds}[a1]",
            "[0:a][a1]amix=inputs=2[aout]",
        });

        await ToolRunner.RunAsync(
            "ffmpeg",
            new ToolRunner.CommandProgressInfo("replace", $"Splicing {engineName} into target clip", null, Path.GetFileName(targetClipPath), "replace"),
            "-y",
            "-i",
            mutedTargetPath,
            "-i",
            normalizedReplacementPath,
            "-filter_complex",
            filter,
            "-map",
            "[aout]",
            outputPath);
    }

    private static async Task MuteTargetWindowAsync(string targetClipPath, string outputPath, string mutedStartSeconds, string mutedEndSeconds, string engineName)
    {
        await ToolRunner.RunAsync(
            "ffmpeg",
            new ToolRunner.CommandProgressInfo("replace", $"Muting target window for {engineName}", null, Path.GetFileName(targetClipPath), "replace"),
            "-y",
            "-i",
            targetClipPath,
            "-af",
            $"volume=enable='between(t,{mutedStartSeconds},{mutedEndSeconds})':volume={ReplacementMuteVolume.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}",
            outputPath);
    }

    private static async Task MatchLoudnessAsync(
        string targetClipPath,
        string replacementClipPath,
        string outputPath,
        double relativeWordStart,
        double relativeWordEnd,
        string engineName)
    {
        if (string.Equals(engineName, "CosyVoice", StringComparison.OrdinalIgnoreCase))
        {
            await ApplyCosyVoiceGainAsync(replacementClipPath, outputPath);
            return;
        }

        var targetRms = await ProbeRmsAsync(targetClipPath, relativeWordStart, relativeWordEnd);
        var replacementRms = await ProbeRmsAsync(replacementClipPath, null, null);

        if (targetRms is null || replacementRms is null || targetRms <= 0 || replacementRms <= 0)
        {
            File.Copy(replacementClipPath, outputPath, overwrite: true);
            return;
        }

        var gainDb = 20.0 * Math.Log10(targetRms.Value / replacementRms.Value);
        gainDb = Math.Clamp(gainDb, -MaximumLoudnessGainDb, MaximumLoudnessGainDb);

        if (Math.Abs(gainDb) < 0.5)
        {
            File.Copy(replacementClipPath, outputPath, overwrite: true);
            return;
        }

        await ToolRunner.RunAsync(
            "ffmpeg",
            new ToolRunner.CommandProgressInfo("replace", $"Matching {engineName} loudness", $"gain {gainDb:F1} dB", Path.GetFileName(replacementClipPath), "replace"),
            "-y",
            "-i",
            replacementClipPath,
            "-af",
            $"volume={gainDb.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}dB",
            outputPath);
    }

    private static async Task ApplyCosyVoiceGainAsync(string replacementClipPath, string outputPath)
    {
        await ToolRunner.RunAsync(
            "ffmpeg",
            new ToolRunner.CommandProgressInfo("replace", "Applying CosyVoice direct gain", $"gain {CosyVoiceDirectGainDb:F1} dB", Path.GetFileName(replacementClipPath), "replace"),
            "-y",
            "-i",
            replacementClipPath,
            "-af",
            $"volume={CosyVoiceDirectGainDb.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}dB,alimiter=limit=0.95",
            outputPath);
    }

    private static async Task<double?> ProbeRmsAsync(string inputPath, double? startSeconds, double? endSeconds)
    {
        var args = new List<string> { "-v", "error" };
        if (startSeconds.HasValue)
        {
            args.AddRange(["-ss", startSeconds.Value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)]);
        }

        if (startSeconds.HasValue && endSeconds.HasValue && endSeconds.Value > startSeconds.Value)
        {
            args.AddRange(["-t", (endSeconds.Value - startSeconds.Value).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)]);
        }

        args.AddRange([
            "-i",
            inputPath,
            "-af",
            "astats=metadata=1:reset=1",
            "-f",
            "null",
            "-"
        ]);

        var result = await ToolRunner.RunCaptureAsync("ffmpeg", args.ToArray());
        var combined = string.Join(Environment.NewLine, new[] { result.StandardOutput, result.StandardError });
        var match = System.Text.RegularExpressions.Regex.Matches(combined, @"RMS level dB:\s*(-?\d+(?:\.\d+)?)")
            .Select(item => double.TryParse(item.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : double.NaN)
            .Where(value => !double.IsNaN(value) && !double.IsInfinity(value))
            .DefaultIfEmpty(double.NaN)
            .Last();

        if (double.IsNaN(match))
        {
            return null;
        }

        return Math.Pow(10.0, match / 20.0);
    }

    public static async Task<double> ProbeAudioDurationAsync(string path)
    {
        var mediaInfo = await MediaInspector.InspectAsync(path);
        return mediaInfo.DurationSeconds;
    }

    private static string BuildAtempoFilter(double ratio)
    {
        var factors = new List<double>();
        var remaining = ratio;
        while (remaining > 2.0)
        {
            factors.Add(2.0);
            remaining /= 2.0;
        }

        while (remaining < 0.5)
        {
            factors.Add(0.5);
            remaining /= 0.5;
        }

        factors.Add(remaining);
        return string.Join(',', factors.Select(value => $"atempo={value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}"));
    }
}