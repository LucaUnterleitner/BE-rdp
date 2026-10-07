using System.Text.RegularExpressions;
using RdpManager.Core.Models;

namespace RdpManager.Core.Validation;

/// <summary>Cleans settings before they are saved (port of Store.saveSettings).</summary>
public static partial class SettingsNormalizer
{
    [GeneratedRegex("[^0-9a-fA-F]", RegexOptions.CultureInvariant)]
    private static partial Regex NonHexRe();

    public static readonly int[] RefreshChoices = [30, 60, 120, 300, 600];

    public static AppSettings Normalize(AppSettings input)
    {
        var s = (input ?? new AppSettings()).Clone();
        s.Defaults = ConnectionNormalizer.NormalizeDefaults(s.Defaults);
        s.LaunchMode = s.LaunchMode == "direct" ? "direct" : "file";
        s.SigningThumbprint = CleanThumbprint(s.SigningThumbprint);
        s.StatusRefreshSeconds = Math.Min(600, Math.Max(30, s.StatusRefreshSeconds <= 0 ? 60 : s.StatusRefreshSeconds));
        s.SessionWindow = s.SessionWindow == "external" ? "external" : "tabs";
        s.View = s.View == "list" ? "list" : "grid";
        return s;
    }

    public static string CleanThumbprint(string? value) => NonHexRe().Replace(value ?? "", "").ToUpperInvariant();
}
