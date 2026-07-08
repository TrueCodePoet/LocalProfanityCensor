using LocalProfanityCensor.DotNet.Models;

namespace LocalProfanityCensor.DotNet.Services;

internal static class CensorRangeBuilder
{
    private static readonly Dictionary<string, int> ActionPriority = new(StringComparer.OrdinalIgnoreCase)
    {
        ["beep"] = 4,
        ["mute"] = 3,
        ["duck"] = 2,
        ["tone"] = 1,
    };

    public static List<CensorRange> BuildCensorRanges(List<ProfanityMatch> matches, double duration, int paddingStartMs, int paddingEndMs, int mergeGapMs)
    {
        var rawRanges = new List<CensorRange>();
        foreach (var match in matches)
        {
            var start = Math.Max(0.0, match.Start - (paddingStartMs / 1000.0));
            var end = Math.Min(duration, match.End + (paddingEndMs / 1000.0));
            rawRanges.Add(new CensorRange
            {
                Start = start,
                End = end,
                Action = match.Action,
                Matches = [match],
            });
        }

        if (rawRanges.Count == 0)
        {
            return [];
        }

        rawRanges = rawRanges.OrderBy(item => item.Start).ThenBy(item => item.End).ToList();
        var merged = new List<CensorRange> { rawRanges[0] };
        var mergeGapSeconds = mergeGapMs / 1000.0;

        foreach (var current in rawRanges.Skip(1))
        {
            var previous = merged[^1];
            if (current.Start <= previous.End || (current.Start - previous.End) <= mergeGapSeconds)
            {
                previous.End = Math.Max(previous.End, current.End);
                previous.Matches.AddRange(current.Matches);
                previous.Action = HigherPriorityAction(previous.Action, current.Action);
                continue;
            }

            merged.Add(current);
        }

        return merged;
    }

    private static string HigherPriorityAction(string left, string right)
    {
        return ActionPriority.GetValueOrDefault(left, 0) >= ActionPriority.GetValueOrDefault(right, 0) ? left : right;
    }
}