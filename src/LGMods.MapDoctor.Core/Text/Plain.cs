namespace LGMods.MapDoctor.Core.Text;

public static class Plain
{
    public static string JoinAnd(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
        };
    }

    // "A, B and C", or "A, B, C and 27 others" when the list is long.
    // With a noun: "A and B maps", "A, B, C and 2 other maps".
    public static string JoinAndShort(IReadOnlyList<string> items, int show = 3, string? noun = null)
    {
        if (items.Count <= show + 1)
            return noun is null ? JoinAnd(items) : $"{JoinAnd(items)} {(items.Count == 1 ? noun : noun + "s")}";
        var rest = items.Count - show;
        var others = noun is null ? (rest == 1 ? "other" : "others") : $"other {(rest == 1 ? noun : noun + "s")}";
        return $"{string.Join(", ", items.Take(show))} and {rest} {others}";
    }

    // "A, B, C, D and E", or "A, B, C, D, E and 6 more" (never two "and"s).
    public static string JoinAndMore(IReadOnlyList<string> items, int show = 5) =>
        items.Count <= show + 1 ? JoinAnd(items) : $"{string.Join(", ", items.Take(show))} and {items.Count - show} more";

    public static string Count(int n, string singular, string? plural = null) =>
        $"{n:N0} {(n == 1 ? singular : plural ?? singular + "s")}";

    public static string Size(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.0} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.0} MB",
        >= 1024 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} bytes",
    };
}
