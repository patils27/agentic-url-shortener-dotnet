// Click analytics aggregation (pure functions, no I/O).

namespace AgenticUrlShortener.Service;

public static class ClickAnalytics
{
    /// <summary>Aggregate raw click rows into a <see cref="UrlStats"/> response.</summary>
    public static UrlStats BuildStats(string code, List<ClickRow> clicks)
    {
        var byDay = new Dictionary<string, int>();
        var referrers = new Dictionary<string, int>();
        var userAgents = new Dictionary<string, int>();
        string? lastClicked = null;

        foreach (var click in clicks)
        {
            var ts = click.Ts ?? string.Empty;
            var day = ts.Length >= 10 ? ts[..10] : ts;
            byDay[day] = byDay.GetValueOrDefault(day) + 1;
            if (!string.IsNullOrEmpty(click.Referrer))
                referrers[click.Referrer] = referrers.GetValueOrDefault(click.Referrer) + 1;
            if (!string.IsNullOrEmpty(click.UserAgent))
                userAgents[click.UserAgent] = userAgents.GetValueOrDefault(click.UserAgent) + 1;
            if (lastClicked is null || string.CompareOrdinal(ts, lastClicked) > 0)
                lastClicked = ts;
        }

        return new UrlStats
        {
            Code = code,
            TotalClicks = clicks.Count,
            ClicksByDay = byDay.OrderBy(kv => kv.Key)
                               .Select(kv => new DayCount { Date = kv.Key, Count = kv.Value })
                               .ToList(),
            Referrers = referrers,
            UserAgents = userAgents,
            LastClickedAt = lastClicked,
        };
    }
}
