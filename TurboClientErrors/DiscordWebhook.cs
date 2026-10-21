using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TurboClientErrors.Ingest;

namespace TurboClientErrors;

/// <summary>
/// Posts each new issue to <see cref="ClientErrorsConfig.DiscordWebhookUrl"/>: nothing when the URL
/// is empty, never an issue seen before, and at most <see cref="ClientErrorsConfig.DiscordMaxPerHour"/>
/// an hour (those over it are counted in the next post). A failed post is logged; the issue is in
/// the table either way. When the host has an admin panel URL (<c>Turbo:Admin:PanelUrl</c>) the post
/// links to the issue's page.
/// </summary>
public sealed class DiscordWebhook(
    IOptionsMonitor<ClientErrorsConfig> options,
    TimeProvider time,
    ILogger<DiscordWebhook> logger,
    IConfiguration? configuration = null,
    HttpMessageHandler? handler = null
) : IDisposable
{
    private const int MAX_CONTENT = 1900;
    private const int MAX_FRAMES = 5;

    private readonly HttpClient _http = handler is null
        ? new HttpClient { Timeout = TimeSpan.FromSeconds(15) }
        : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Lock _lock = new();
    private DateTimeOffset _windowStart = DateTimeOffset.MinValue;
    private int _sentInWindow;
    private int _skipped;

    /// <summary>
    /// Posts <paramref name="issue"/> unless no webhook is set or the hour's posts are spent: true when
    /// it was sent and Discord took it.
    /// </summary>
    public async Task<bool> PostNewAsync(NewIssue issue)
    {
        var config = options.CurrentValue;

        if (string.IsNullOrWhiteSpace(config.DiscordWebhookUrl))
            return false;

        int skipped;

        lock (_lock)
        {
            var now = time.GetUtcNow();

            if (now - _windowStart >= TimeSpan.FromHours(1))
            {
                _windowStart = now;
                _sentInWindow = 0;
            }

            if (config.DiscordMaxPerHour > 0 && _sentInWindow >= config.DiscordMaxPerHour)
            {
                _skipped++;

                return false;
            }

            _sentInWindow++;
            skipped = _skipped;
            _skipped = 0;
        }

        return await SendAsync(config.DiscordWebhookUrl, Content(issue, skipped))
            .ConfigureAwait(false);
    }

    internal string Content(NewIssue @new, int skipped)
    {
        var (issue, report) = @new;
        var panel = configuration?["Turbo:Admin:PanelUrl"]?.TrimEnd('/');
        var frames = string.Join(
            '\n',
            Fingerprints.Frames(report.Stack).Take(MAX_FRAMES).Select(x => x.ToString())
        );
        var builder = new StringBuilder()
            .Append($"**New client error #{issue.Id}** ({issue.Kind})")
            .Append($" - build `{report.Build}`, {UserAgents.Browser(report.UserAgent)}")
            .Append($" on {UserAgents.Platform(report.UserAgent, report.Platform)}\n")
            .Append($"```\n{Fence(issue.Message)}\n");

        if (frames.Length > 0)
            builder.Append($"{Fence(frames)}\n");

        builder.Append("```");

        if (!string.IsNullOrEmpty(panel))
            builder.Append($"\n{panel}/plugin/clienterrors/issue/{issue.Id}");

        if (skipped > 0)
            builder.Append($"\n({skipped} more new issues were not posted: DiscordMaxPerHour)");

        var content = builder.ToString();

        return content.Length > MAX_CONTENT ? content[..MAX_CONTENT] : content;
    }

    /// <summary>A code block cannot be closed from inside it.</summary>
    private static string Fence(string text) => text.Replace("```", "'''");

    private async Task<bool> SendAsync(string url, string content)
    {
        try
        {
            var payload = JsonSerializer.Serialize(
                new { content, allowed_mentions = new { parse = Array.Empty<string>() } }
            );
            using var body = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, body).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                logger.LogWarning(
                    "Discord webhook answered {Status} for a new client error",
                    (int)response.StatusCode
                );

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Discord webhook failed for a new client error");

            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
