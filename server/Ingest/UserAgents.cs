using System;
using System.Text.RegularExpressions;

namespace TurboClientErrors.Ingest;

/// <summary>
/// A browser and an operating system from a user agent, as broad as a report needs: the family and
/// major version (<c>Chrome 141</c>), and the system's family (<c>Windows</c>). Never the whole
/// string, which is kept on the occurrence alone.
/// </summary>
public static partial class UserAgents
{
    public static string Browser(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return "Unknown";

        // Most specific first: Edge and Opera say Chrome too, Chrome says Safari.
        foreach (
            var (name, pattern) in new (string, Regex)[]
            {
                ("Edge", Edge()),
                ("Opera", Opera()),
                ("Samsung Internet", Samsung()),
                ("Firefox", Firefox()),
                ("Chrome", Chrome()),
                ("Safari", Safari()),
            }
        )
        {
            var match = pattern.Match(userAgent);

            if (match.Success)
                return $"{name} {match.Groups["v"].Value}";
        }

        return "Other";
    }

    /// <summary>The system the user agent names, else the one <c>navigator.platform</c> names.</summary>
    public static string Platform(string? userAgent, string? platform) =>
        System(userAgent) ?? System(platform) ?? "Other";

    private static string? System(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (text.Contains("Android", StringComparison.OrdinalIgnoreCase))
            return "Android";

        if (
            text.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
            || text.Contains("iPad", StringComparison.OrdinalIgnoreCase)
            || text.Contains("iOS", StringComparison.Ordinal)
        )
            return "iOS";

        if (text.Contains("Windows", StringComparison.OrdinalIgnoreCase) || text.Contains("Win32"))
            return "Windows";

        if (text.Contains("CrOS", StringComparison.Ordinal))
            return "ChromeOS";

        if (
            text.Contains("Mac OS", StringComparison.OrdinalIgnoreCase)
            || text.Contains("macOS", StringComparison.OrdinalIgnoreCase)
            || text.Contains("MacIntel", StringComparison.Ordinal)
        )
            return "macOS";

        if (text.Contains("Linux", StringComparison.OrdinalIgnoreCase))
            return "Linux";

        return null;
    }

    [GeneratedRegex(@"Edg(?:e|A|iOS)?/(?<v>\d+)")]
    private static partial Regex Edge();

    [GeneratedRegex(@"(?:OPR|Opera)/(?<v>\d+)")]
    private static partial Regex Opera();

    [GeneratedRegex(@"SamsungBrowser/(?<v>\d+)")]
    private static partial Regex Samsung();

    [GeneratedRegex(@"(?:Firefox|FxiOS)/(?<v>\d+)")]
    private static partial Regex Firefox();

    [GeneratedRegex(@"(?:Chrome|CriOS)/(?<v>\d+)")]
    private static partial Regex Chrome();

    [GeneratedRegex(@"Version/(?<v>\d+)[.\d]* (?:Mobile/\S+ )?Safari/")]
    private static partial Regex Safari();
}
