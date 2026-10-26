using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Turbo.Primitives.Plugins.Events;
using TurboClientErrors.Ingest;

namespace TurboClientErrors;

/// <summary>
/// Tells other plugins of new and reopened issues with a <see cref="PluginNoticeEvent"/> on the
/// host's event system (turbo-cloud's <c>docs/plugin-notices.md</c>): the Discord plugin posts them
/// as <c>plugin:clienterrors:issue.new</c> and <c>plugin:clienterrors:issue.reopened</c>. Nothing is
/// published until the plugin's start sets <see cref="Publisher"/>, and a publish that throws is
/// logged: the issue is stored either way. When the host has an admin panel URL
/// (<c>Turbo:Admin:PanelUrl</c>) a notice links to the issue's page.
/// </summary>
public sealed class Notices(ILogger<Notices> logger, IConfiguration? configuration = null)
{
    public const string SOURCE = "clienterrors";
    public const string ISSUE_NEW = "issue.new";
    public const string ISSUE_REOPENED = "issue.reopened";

    private const int MAX_FRAMES = 5;

    /// <summary>The host's <c>IEventPublisher.PublishAsync</c> while the plugin runs; null: nothing is published.</summary>
    public Func<PluginNoticeEvent, CancellationToken, Task>? Publisher { get; set; }

    public Task NewAsync(NewIssue issue, CancellationToken ct) =>
        PublishAsync(Issue(issue, reopened: false), ct);

    public Task ReopenedAsync(NewIssue issue, CancellationToken ct) =>
        PublishAsync(Issue(issue, reopened: true), ct);

    /// <summary>Publishes <paramref name="notice"/> cut to the event's limits; never throws.</summary>
    public async Task PublishAsync(PluginNoticeEvent notice, CancellationToken ct)
    {
        if (Publisher is not { } publish)
            return;

        try
        {
            await publish(Fit(notice), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Plugin notice {Topic} {Key} could not be published",
                notice.Topic,
                notice.Key
            );
        }
    }

    /// <summary>
    /// The notice of a new issue (keyed by its id), or of one that came back on a new build (keyed
    /// by its id and that build: an issue may come back again on a later build).
    /// </summary>
    internal PluginNoticeEvent Issue(NewIssue @new, bool reopened)
    {
        var (issue, report) = @new;
        var build = reopened ? issue.ReopenedBuild ?? report.Build : report.Build;
        var id = issue.Id.ToString(CultureInfo.InvariantCulture);
        var frames = string.Join(
            '\n',
            Fingerprints.Frames(report.Stack).Take(MAX_FRAMES).Select(x => x.ToString())
        );
        var panel = configuration?["Turbo:Admin:PanelUrl"]?.TrimEnd('/');

        return new PluginNoticeEvent
        {
            Source = SOURCE,
            Topic = reopened ? ISSUE_REOPENED : ISSUE_NEW,
            Title = reopened
                ? $"Client error #{id} is back on build {build}"
                : $"New client error #{id}",
            Text = frames.Length > 0 ? $"{issue.Message}\n{frames}" : issue.Message,
            Url = string.IsNullOrEmpty(panel) ? null : $"{panel}/plugin/clienterrors/issue/{id}",
            Fields =
            [
                new("Kind", issue.Kind),
                new("Build", build),
                new("Players", issue.Players.ToString(CultureInfo.InvariantCulture)),
                new("Count", issue.Occurrences.ToString(CultureInfo.InvariantCulture)),
            ],
            Level = reopened ? PluginNoticeLevel.Warning : PluginNoticeLevel.Error,
            Key = reopened ? $"{id}@{build}" : id,
        };
    }

    /// <summary>The notice within <see cref="PluginNoticeEvent"/>'s limits.</summary>
    internal static PluginNoticeEvent Fit(PluginNoticeEvent notice) =>
        notice with
        {
            Topic = Cut(notice.Topic, PluginNoticeEvent.MAX_TOPIC_LENGTH),
            Title = Cut(notice.Title, PluginNoticeEvent.MAX_TITLE_LENGTH),
            Text = notice.Text is null ? null : Cut(notice.Text, PluginNoticeEvent.MAX_TEXT_LENGTH),
            Fields = notice.Fields.IsDefaultOrEmpty
                ? []
                : [.. notice.Fields.Take(PluginNoticeEvent.MAX_FIELDS)],
        };

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
