namespace LocalProfanityCensor.DotNet.Models;

internal sealed class ProgressUpdate
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public string Stage { get; init; } = "working";
    public string Message { get; init; } = string.Empty;
    public string? FileName { get; init; }
    public string? Mode { get; init; }
    public int? Current { get; init; }
    public int? Total { get; init; }
    public double? Percent { get; init; }
    public double? MediaTimeSeconds { get; init; }
    public string? Detail { get; init; }
}