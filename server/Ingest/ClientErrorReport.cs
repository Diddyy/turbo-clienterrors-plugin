using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace TurboClientErrors.Ingest;

/// <summary>One breadcrumb: milliseconds since the client started, a category and what happened.</summary>
public sealed record Breadcrumb(long T, string C, string M);

/// <summary>
/// One error report as the server keeps it: the client's JSON read field by field, every text cut
/// to its column and anything unknown left out. <see cref="TryRead"/> takes nothing it cannot read.
/// </summary>
public sealed record ClientErrorReport
{
    public const int MAX_MESSAGE = 1000;
    public const int MAX_COMPONENT_STACK = 8000;
    public const int MAX_BREADCRUMBS = 30;
    public const int MAX_BREADCRUMB_TEXT = 120;
    public const int MAX_GPU = 1024;

    /// <summary>How a client may say it caught an error; anything else is read as <c>error</c>.</summary>
    public static readonly IReadOnlySet<string> KINDS = new HashSet<string>
    {
        "error",
        "rejection",
        "react",
        "renderer",
        "logged",
    };

    public required string Kind { get; init; }

    /// <summary><c>name: message</c>, as the browser prints an uncaught error.</summary>
    public required string Message { get; init; }

    public string? Stack { get; init; }

    public string? ComponentStack { get; init; }

    public string? Source { get; init; }

    public required string Build { get; init; }

    public string? Production { get; init; }

    public string? UserAgent { get; init; }

    public string? Platform { get; init; }

    public string? Page { get; init; }

    public int? RoomId { get; init; }

    public string? Viewport { get; init; }

    public string? Renderer { get; init; }

    public long UptimeMs { get; init; }

    /// <summary>
    /// What the renderer and the page were when the GPU was lost, for a <c>renderer</c> report
    /// (its <c>gpu</c>), in one line: <see cref="ReadGpu"/>.
    /// </summary>
    public string? Gpu { get; init; }

    public IReadOnlyList<Breadcrumb> Breadcrumbs { get; init; } = [];

    /// <summary>The report in <paramref name="json"/>; false when it is not a JSON object with a message.</summary>
    public static bool TryRead(
        ReadOnlySpan<byte> json,
        int maxStackLength,
        out ClientErrorReport report
    )
    {
        report = null!;

        try
        {
            using var document = JsonDocument.Parse(
                json.ToArray(),
                new JsonDocumentOptions { MaxDepth = 8 }
            );
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return false;

            var name = Text(root, "name", 128);
            var message = Text(root, "message", MAX_MESSAGE);

            if (string.IsNullOrWhiteSpace(message) && string.IsNullOrWhiteSpace(name))
                return false;

            var kind = Text(root, "kind", 16)?.ToLowerInvariant();

            report = new ClientErrorReport
            {
                Kind = kind is not null && KINDS.Contains(kind) ? kind : "error",
                Message = Cut(
                    string.IsNullOrEmpty(name)
                    || (message ?? string.Empty).StartsWith(name, StringComparison.Ordinal)
                        ? message ?? name!
                        : $"{name}: {message}",
                    MAX_MESSAGE
                ),
                Stack = Text(root, "stack", Math.Max(0, maxStackLength)),
                ComponentStack = Text(root, "componentStack", MAX_COMPONENT_STACK),
                Source = Text(root, "source", 512),
                Build = Text(root, "build", 64) is { Length: > 0 } build ? build : "unknown",
                Production = Text(root, "production", 64),
                UserAgent = Text(root, "userAgent", 512),
                Platform = Text(root, "platform", 64),
                Page = WithoutQuery(Text(root, "page", 2048)) is { } page ? Cut(page, 256) : null,
                RoomId = Number(root, "roomId") is { } room and > 0 and <= int.MaxValue
                    ? (int)room
                    : null,
                Viewport = ReadViewport(root),
                Renderer = Text(root, "renderer", 32),
                UptimeMs = Math.Max(0, Number(root, "uptimeMs") ?? 0),
                Gpu = ReadGpu(root),
                Breadcrumbs = ReadBreadcrumbs(root),
            };

            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The breadcrumbs as stored: compact JSON.</summary>
    public string? BreadcrumbsJson() =>
        Breadcrumbs.Count == 0 ? null : JsonSerializer.Serialize(Breadcrumbs, JSON);

    /// <summary>Stored breadcrumbs, read back; none when they cannot be read.</summary>
    public static IReadOnlyList<Breadcrumb> ReadBreadcrumbs(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<Breadcrumb>>(json, JSON) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static readonly JsonSerializerOptions JSON = new(JsonSerializerDefaults.Web);

    internal static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    private static string? Text(JsonElement root, string name, int max) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Clean(value.GetString(), max)
            : null;

    /// <summary>Control characters but line breaks and tabs out, cut to <paramref name="max"/>; null when empty.</summary>
    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || max <= 0)
            return null;

        var cut = Cut(value, max);
        var builder = new StringBuilder(cut.Length);

        foreach (var c in cut)
            if (!char.IsControl(c) || c is '\n' or '\t')
                builder.Append(c);

        var clean = builder.ToString().Trim();

        return clean.Length == 0 ? null : clean;
    }

    private static long? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
        && double.IsFinite(number)
        && Math.Abs(number) < 9e15
            ? (long)number
            : null;

    /// <summary>A page without its query and fragment: a login ticket must never be kept.</summary>
    internal static string? WithoutQuery(string? page)
    {
        if (page is null)
            return null;

        var end = page.IndexOfAny(['?', '#']);

        return end < 0 ? page : page[..end];
    }

    private static string? ReadViewport(JsonElement root)
    {
        if (
            !root.TryGetProperty("viewport", out var viewport)
            || viewport.ValueKind != JsonValueKind.Object
        )
            return null;

        var width = Number(viewport, "width");
        var height = Number(viewport, "height");

        if (width is not (> 0 and < 100000) || height is not (> 0 and < 100000))
            return null;

        var ratio =
            viewport.TryGetProperty("pixelRatio", out var r)
            && r.ValueKind == JsonValueKind.Number
            && r.TryGetDouble(out var value)
            && value is > 0 and < 100
                ? value
                : 1;

        return $"{width}x{height}@{ratio.ToString("0.##", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// The report's <c>gpu</c> as one line: the GPU's name; the browser's reason and message for the
    /// loss; the page's visibility and focus; the renderer's textures, render textures and their
    /// estimated size; the JS heap. Null when it says none of them.
    /// </summary>
    private static string? ReadGpu(JsonElement root)
    {
        if (!root.TryGetProperty("gpu", out var gpu) || gpu.ValueKind != JsonValueKind.Object)
            return null;

        var parts = new List<string>();

        if (Text(gpu, "name", 256) is { } name)
            parts.Add(name);

        var lost = string.Join(
            ": ",
            new[] { Text(gpu, "reason", 32), Text(gpu, "message", 256) }.Where(x => x is not null)
        );

        if (lost.Length > 0)
            parts.Add($"lost: {lost}");

        if (Text(gpu, "visibility", 16) is { } visibility)
            parts.Add(
                gpu.TryGetProperty("focused", out var focused)
                && focused.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? $"page {visibility}, {(focused.GetBoolean() ? "focused" : "not focused")}"
                    : $"page {visibility}"
            );

        if (Number(gpu, "textures") is { } textures and >= 0)
        {
            var render = Number(gpu, "renderTextures") is { } r and >= 0 ? $" ({r} render)" : "";
            var size = Number(gpu, "textureBytes") is { } bytes and >= 0
                ? $", {Megabytes(bytes)}"
                : "";

            parts.Add($"{textures} textures{render}{size}");
        }

        if (Number(gpu, "heapBytes") is { } heap and >= 0)
            parts.Add(
                Number(gpu, "heapLimitBytes") is { } limit and > 0
                    ? $"JS heap {Megabytes(heap)} of {Megabytes(limit)}"
                    : $"JS heap {Megabytes(heap)}"
            );

        return parts.Count == 0 ? null : Cut(string.Join(" | ", parts), MAX_GPU);
    }

    private static string Megabytes(long bytes) =>
        $"{(bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture)} MB";

    private static IReadOnlyList<Breadcrumb> ReadBreadcrumbs(JsonElement root)
    {
        if (
            !root.TryGetProperty("breadcrumbs", out var list)
            || list.ValueKind != JsonValueKind.Array
        )
            return [];

        var crumbs = new List<Breadcrumb>();

        // The last ones: those just before the error.
        var skip = Math.Max(0, list.GetArrayLength() - MAX_BREADCRUMBS);

        foreach (var item in list.EnumerateArray())
        {
            if (skip-- > 0 || item.ValueKind != JsonValueKind.Object)
                continue;

            var what = Text(item, "m", MAX_BREADCRUMB_TEXT);

            if (what is null)
                continue;

            crumbs.Add(new Breadcrumb(Number(item, "t") ?? 0, Text(item, "c", 16) ?? "ui", what));
        }

        return crumbs;
    }
}
