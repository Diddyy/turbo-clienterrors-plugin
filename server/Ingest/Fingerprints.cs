using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TurboClientErrors.Ingest;

/// <summary>One stack frame: the function (when the browser named one), the file, line and column.</summary>
public sealed record StackFrame(string Function, string File, int Line, int Column)
{
    public override string ToString() =>
        string.IsNullOrEmpty(Function)
            ? $"{File}:{Line}:{Column}"
            : $"{Function} ({File}:{Line}:{Column})";
}

/// <summary>
/// How reports are told apart. Two reports are one <b>fingerprint</b> when their normalised message,
/// top frames (function, file, line, column) and build agree; the frames of a minified build only
/// mean something within that build. Their <b>signature</b> leaves the build, the lines and the
/// files' content hashes out, so the same error on the next build finds the issue it had.
/// </summary>
public static partial class Fingerprints
{
    /// <summary>The fingerprint of <paramref name="report"/>, 40 hex characters.</summary>
    public static string Of(ClientErrorReport report, int frames)
    {
        var top = Frames(report.Stack).Take(Math.Max(0, frames)).ToList();
        var where =
            top.Count > 0
                ? string.Join('\n', top.Select(x => $"{x.Function}@{x.File}:{x.Line}:{x.Column}"))
                : NormaliseFile(report.Source ?? string.Empty);

        return Hash($"{report.Build}\n{NormaliseMessage(report.Message)}\n{where}");
    }

    /// <summary>The signature of <paramref name="report"/>, 40 hex characters.</summary>
    public static string Signature(ClientErrorReport report, int frames)
    {
        var files = Frames(report.Stack)
            .Take(Math.Max(0, frames))
            .Select(x => WithoutContentHash(x.File));

        return Hash($"{NormaliseMessage(report.Message)}\n{string.Join('\n', files)}");
    }

    /// <summary>
    /// The message with what changes from one occurrence to the next taken out: numbers, ids,
    /// hexadecimal, the query of a URL, runs of white space.
    /// </summary>
    public static string NormaliseMessage(string message)
    {
        var text = message.Trim();

        if (text.StartsWith("Uncaught ", StringComparison.Ordinal))
            text = text["Uncaught ".Length..];

        text = UrlPattern().Replace(text, m => NormaliseFile(m.Value));
        text = UuidPattern().Replace(text, "<uuid>");
        text = HexPattern().Replace(text, "<hex>");
        text = NumberPattern().Replace(text, "<n>");
        text = SpacePattern().Replace(text, " ");

        return text.Length <= 300 ? text : text[..300];
    }

    /// <summary>The frames of a stack as Chrome, Edge, Firefox and Safari write them; lines that are not frames are skipped.</summary>
    public static IEnumerable<StackFrame> Frames(string? stack)
    {
        if (string.IsNullOrEmpty(stack))
            yield break;

        foreach (var raw in stack.Split('\n'))
        {
            var line = raw.Trim();
            var match = ChromeFrame().Match(line);

            if (!match.Success)
                match = GeckoFrame().Match(line);

            if (!match.Success)
                continue;

            yield return new StackFrame(
                match.Groups["fn"].Value.Trim(),
                NormaliseFile(match.Groups["file"].Value),
                int.TryParse(match.Groups["line"].Value, out var l) ? l : 0,
                int.TryParse(match.Groups["col"].Value, out var c) ? c : 0
            );
        }
    }

    /// <summary>A script's path without its origin, query and fragment: the same file whichever host served it.</summary>
    public static string NormaliseFile(string file)
    {
        var path = file.Trim();
        var scheme = path.IndexOf("://", StringComparison.Ordinal);

        if (scheme >= 0)
        {
            var slash = path.IndexOf('/', scheme + 3);

            path = slash < 0 ? "/" : path[slash..];
        }

        return ClientErrorReport.WithoutQuery(path) ?? path;
    }

    /// <summary><c>/assets/index-B3x9_kQa.js</c> as <c>/assets/index.js</c>: a bundler's content hash changes with each build.</summary>
    public static string WithoutContentHash(string file) => ContentHash().Replace(file, "$1$2");

    private static string Hash(string text) =>
        Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(text)));

    // `at fn (file:1:2)`, `at file:1:2`, and code run by eval: `at fn (eval at caller (file:1:2), <anonymous>:3:4)`.
    [GeneratedRegex(
        @"^at (?:(?<fn>.+?) \()?(?:eval at .*, )?(?<file>[^()\s]+?):(?<line>\d+):(?<col>\d+)\)?$"
    )]
    private static partial Regex ChromeFrame();

    [GeneratedRegex(@"^(?<fn>[^@\s]*)@(?<file>.+?):(?<line>\d+):(?<col>\d+)$")]
    private static partial Regex GeckoFrame();

    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]*://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(
        @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex UuidPattern();

    [GeneratedRegex(@"\b(?:0x[0-9a-f]+|(?=[0-9a-f]*\d)[0-9a-f]{8,})\b", RegexOptions.IgnoreCase)]
    private static partial Regex HexPattern();

    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacePattern();

    [GeneratedRegex(@"([\w.]+?)[-.][A-Za-z0-9_-]{8,}(\.m?js)$")]
    private static partial Regex ContentHash();
}
