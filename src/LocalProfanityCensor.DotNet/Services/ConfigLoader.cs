using LocalProfanityCensor.DotNet.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LocalProfanityCensor.DotNet.Services;

internal static class ConfigLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static AppConfig Load(string? configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            return new AppConfig();
        }

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"Config file was not found: {configPath}", configPath);
        }

        var yaml = File.ReadAllText(configPath);
        return Deserializer.Deserialize<AppConfig>(yaml) ?? new AppConfig();
    }

    public static AppConfig LoadRuntime(string? configPath, string? dictionaryPath, bool dryRun, bool keepWork, string? mode)
    {
        var config = Load(configPath);
        if (!string.IsNullOrWhiteSpace(dictionaryPath))
        {
            config.DictionaryPath = dictionaryPath;
        }

        if (dryRun)
        {
            config.DryRun = true;
        }

        if (keepWork)
        {
            config.KeepWork = true;
        }

        if (!string.IsNullOrWhiteSpace(mode))
        {
            config.Censor.Mode = mode;
        }

        return config;
    }
}