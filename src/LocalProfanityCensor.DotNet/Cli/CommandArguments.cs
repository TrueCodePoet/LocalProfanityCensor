namespace LocalProfanityCensor.DotNet.Cli;

internal sealed class CommandArguments
{
    private readonly List<string> _positionals = [];
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public static CommandArguments Parse(IEnumerable<string> args)
    {
        var parsed = new CommandArguments();
        using var enumerator = args.GetEnumerator();

        while (enumerator.MoveNext())
        {
            var token = enumerator.Current ?? string.Empty;
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                parsed._positionals.Add(token);
                continue;
            }

            var option = token[2..];
            if (option.Contains('=', StringComparison.Ordinal))
            {
                var parts = option.Split('=', 2, StringSplitOptions.None);
                parsed._options[parts[0]] = parts[1];
                continue;
            }

            if (enumerator.MoveNext() && enumerator.Current is { } next && !next.StartsWith("--", StringComparison.Ordinal))
            {
                parsed._options[option] = next;
                continue;
            }

            parsed._flags.Add(option);
        }

        return parsed;
    }

    public string RequirePositional(int index, string message)
    {
        if (index < _positionals.Count)
        {
            return _positionals[index];
        }

        throw new CliUsageException(message);
    }

    public string? GetOption(string name)
    {
        return _options.GetValueOrDefault(name);
    }

    public string RequireOption(string name, string message)
    {
        return GetOption(name) ?? throw new CliUsageException(message);
    }

    public bool HasFlag(string name)
    {
        return _flags.Contains(name) || string.Equals(_options.GetValueOrDefault(name), "true", StringComparison.OrdinalIgnoreCase);
    }
}