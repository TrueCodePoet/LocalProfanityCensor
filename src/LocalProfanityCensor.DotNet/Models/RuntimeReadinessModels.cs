using System.Text.Json.Serialization;

namespace LocalProfanityCensor.DotNet.Models;

internal sealed class RuntimeReadinessResult
{
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "core";

    [JsonPropertyName("is_ready")]
    public bool IsReady { get; set; }

    [JsonPropertyName("bootstrap_script")]
    public string? BootstrapScript { get; set; }

    [JsonPropertyName("missing_items")]
    public List<string> MissingItems { get; set; } = [];

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}