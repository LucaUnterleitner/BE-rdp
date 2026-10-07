using System.Globalization;

namespace RdpManager.Core.Formatting;

/// <summary>Date, duration and name formatting for the UI (port of ui.js helpers).</summary>
public static class Format
{
    public static DateTimeOffset? ParseIso(string? iso)
        => !string.IsNullOrEmpty(iso) && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;

    /// <summary>"Today, 09:14", "Yesterday, 17:02" or "06 Oct 2026, 09:14" in local time.</summary>
    public static string When(DateTimeOffset? value, DateTimeOffset? now = null)
    {
        if (value is null) return "";
        var d = value.Value.ToLocalTime();
        var today = (now ?? DateTimeOffset.Now).ToLocalTime().Date;
        var time = d.ToString("t", CultureInfo.CurrentCulture);
        if (d.Date == today) return $"Today, {time}";
        if (d.Date == today.AddDays(-1)) return $"Yesterday, {time}";
        return $"{d.ToString("dd MMM yyyy", CultureInfo.CurrentCulture)}, {time}";
    }

    public static string When(string? iso) => When(ParseIso(iso));

    public static string DateTime(DateTimeOffset? value)
        => value is null ? "" : value.Value.ToLocalTime().ToString("dd MMM yyyy, t", CultureInfo.CurrentCulture);

    public static string Time(DateTimeOffset? value) => value is null ? "" : value.Value.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    public static string Duration(double seconds)
    {
        var s = (long)Math.Max(0, Math.Round(seconds));
        var h = s / 3600;
        var m = s % 3600 / 60;
        if (h > 0) return $"{h} h {m:00} min";
        if (m > 0) return $"{m} min";
        return $"{s} s";
    }

    /// <summary>Initials for the avatar ("CONTOSO\jane.doe" → "JD").</summary>
    public static string Initials(string? name)
    {
        var parts = (name ?? "").Split([' ', '.', '\\', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        var picked = parts.Skip(Math.Max(0, parts.Length - 2)).Select(p => char.ToUpperInvariant(p[0]));
        var s = string.Concat(picked);
        return s.Length > 0 ? s : "?";
    }

    public static string Plural(int n, string one, string many) => n == 1 ? $"{n} {one}" : $"{n} {many}";
}
