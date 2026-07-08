using System.Text.Json.Serialization;

namespace LocalProfanityCensor.DotNet.Models;

internal sealed class CommandPlan
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("input_path")]
    public string InputPath { get; set; } = string.Empty;

    [JsonPropertyName("output_path")]
    public string OutputPath { get; set; } = string.Empty;

    [JsonPropertyName("dictionary_path")]
    public string DictionaryPath { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("resolved_config")]
    public AppConfig ResolvedConfig { get; set; } = new();
}