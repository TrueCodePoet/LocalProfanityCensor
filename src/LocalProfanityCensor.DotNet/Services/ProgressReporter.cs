using System.Globalization;
using System.Text;
using System.Threading;
using LocalProfanityCensor.DotNet.Models;

namespace LocalProfanityCensor.DotNet.Services;

internal static class ProgressReporter
{
    private static readonly AsyncLocal<Action<string>?> Sink = new();
    private static readonly AsyncLocal<Action<ProgressUpdate>?> StructuredSink = new();

    public static IDisposable BeginConsoleReporting()
    {
        var previousSink = Sink.Value;
        var previousStructuredSink = StructuredSink.Value;
        var renderer = new ConsoleProgressRenderer();
        Sink.Value = message =>
        {
            renderer.Report(new ProgressUpdate
            {
                TimestampUtc = DateTime.UtcNow,
                Stage = "status",
                Message = message,
            });
        };
        StructuredSink.Value = renderer.Report;
        return new RestoreScope(previousSink, previousStructuredSink, renderer);
    }

    public static void Report(string message)
    {
        Sink.Value?.Invoke(message);
    }

    public static void Report(ProgressUpdate update)
    {
        StructuredSink.Value?.Invoke(update);
        if (StructuredSink.Value is null && !string.IsNullOrWhiteSpace(update.Message))
        {
            Sink.Value?.Invoke(update.Message);
        }
    }

    public static void ReportStage(
        string stage,
        string message,
        string? fileName = null,
        string? mode = null,
        int? current = null,
        int? total = null,
        double? percent = null,
        double? mediaTimeSeconds = null,
        string? detail = null)
    {
        Report(new ProgressUpdate
        {
            TimestampUtc = DateTime.UtcNow,
            Stage = stage,
            Message = message,
            FileName = fileName,
            Mode = mode,
            Current = current,
            Total = total,
            Percent = percent,
            MediaTimeSeconds = mediaTimeSeconds,
            Detail = detail,
        });
    }

    private sealed class RestoreScope(Action<string>? previousSink, Action<ProgressUpdate>? previousStructuredSink, ConsoleProgressRenderer renderer) : IDisposable
    {
        public void Dispose()
        {
            Sink.Value = previousSink;
            StructuredSink.Value = previousStructuredSink;
            renderer.Dispose();
        }
    }

    private sealed class ConsoleProgressRenderer : IDisposable
    {
        private readonly object _sync = new();
        private readonly DateTime _startedUtc = DateTime.UtcNow;
        private int _lastLineLength;
        private bool _disposed;

        public void Report(ProgressUpdate update)
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                var line = BuildLine(update);
                if (Console.IsErrorRedirected)
                {
                    Console.Error.WriteLine(line);
                    return;
                }

                var padded = line.Length < _lastLineLength
                    ? line + new string(' ', _lastLineLength - line.Length)
                    : line;
                Console.Error.Write('\r');
                Console.Error.Write(padded);
                _lastLineLength = padded.Length;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                if (!Console.IsErrorRedirected && _lastLineLength > 0)
                {
                    Console.Error.Write('\r');
                    Console.Error.Write(new string(' ', _lastLineLength));
                    Console.Error.Write('\r');
                }

                _disposed = true;
            }
        }

        private string BuildLine(ProgressUpdate update)
        {
            var builder = new StringBuilder();
            builder.Append("[status ");
            builder.Append(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            builder.Append("] ");
            builder.Append(update.Stage);

            if (!string.IsNullOrWhiteSpace(update.Mode))
            {
                builder.Append(" | mode ");
                builder.Append(update.Mode);
            }

            if (!string.IsNullOrWhiteSpace(update.FileName))
            {
                builder.Append(" | file ");
                builder.Append(update.FileName);
            }

            if (update.Current.HasValue && update.Total.HasValue)
            {
                builder.Append(" | ");
                builder.Append(update.Current.Value.ToString(CultureInfo.InvariantCulture));
                builder.Append('/');
                builder.Append(update.Total.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (update.Percent.HasValue)
            {
                builder.Append(" | ");
                builder.Append(update.Percent.Value.ToString("0.0", CultureInfo.InvariantCulture));
                builder.Append('%');
            }

            if (update.MediaTimeSeconds.HasValue)
            {
                builder.Append(" | t ");
                builder.Append(FormatClock(update.MediaTimeSeconds.Value));
            }

            builder.Append(" | elapsed ");
            builder.Append(FormatClock((DateTime.UtcNow - _startedUtc).TotalSeconds));

            if (!string.IsNullOrWhiteSpace(update.Message))
            {
                builder.Append(" | ");
                builder.Append(update.Message);
            }

            if (!string.IsNullOrWhiteSpace(update.Detail))
            {
                builder.Append(" | ");
                builder.Append(update.Detail);
            }

            return builder.ToString();
        }

        private static string FormatClock(double totalSeconds)
        {
            var safeSeconds = Math.Max(0, (int)Math.Floor(totalSeconds));
            var timeSpan = TimeSpan.FromSeconds(safeSeconds);
            return timeSpan.TotalHours >= 1
                ? timeSpan.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
                : timeSpan.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        }
    }
}