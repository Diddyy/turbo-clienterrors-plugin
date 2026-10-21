namespace TurboClientErrors;

/// <summary>
/// The plugin's settings, under <c>Turbo:ClientErrors</c> in the host's configuration, and on the
/// admin panel's Settings page while the plugin is loaded. A change applies at once.
/// </summary>
public sealed class ClientErrorsConfig
{
    public const string SECTION_NAME = "Turbo:ClientErrors";

    /// <summary>
    /// Whether clients are offered the <c>clienterrors</c> extension. Off: no client that logs in
    /// afterwards reports anything, and reports from sessions that accepted it earlier are dropped.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The largest report taken, in bytes of JSON; a larger one is dropped.</summary>
    public int MaxPayloadBytes { get; init; } = 32 * 1024;

    /// <summary>How many reports one session may send per minute (a burst of as many is allowed).</summary>
    public int ReportsPerMinute { get; init; } = 10;

    /// <summary>How many reports one session may send in all; the rest are dropped.</summary>
    public int MaxReportsPerSession { get; init; } = 100;

    /// <summary>How many reports wait for the database at most; more are dropped, never waited for.</summary>
    public int QueueCapacity { get; init; } = 2000;

    /// <summary>How many of the top stack frames tell two errors apart (with the message and the build).</summary>
    public int FingerprintFrames { get; init; } = 5;

    /// <summary>The longest stack kept, in characters; longer is cut.</summary>
    public int MaxStackLength { get; init; } = 16000;

    /// <summary>How many of an issue's latest occurrences (stack, breadcrumbs) are kept; counts are kept in full.</summary>
    public int MaxOccurrencesPerIssue { get; init; } = 50;

    /// <summary>How long an occurrence (stack, breadcrumbs) is kept, in days.</summary>
    public int OccurrenceRetentionDays { get; init; } = 30;

    /// <summary>
    /// How long an issue no client has reported is kept, in days, with its counts and trend. 0: for
    /// ever.
    /// </summary>
    public int IssueRetentionDays { get; init; } = 90;
}
