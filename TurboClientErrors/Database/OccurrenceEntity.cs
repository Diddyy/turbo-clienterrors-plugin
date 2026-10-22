using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TurboClientErrors.Database;

/// <summary>
/// One report of an issue as the client sent it: its stack and breadcrumbs, the build and browser.
/// The latest <c>MaxOccurrencesPerIssue</c> of an issue are kept, for <c>OccurrenceRetentionDays</c>.
/// </summary>
[Table("occurrences")]
[Index(nameof(IssueId), nameof(Id))]
[Index(nameof(OccurredAt))]
public class OccurrenceEntity
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("issue_id")]
    public int IssueId { get; set; }

    /// <summary>When the server took it.</summary>
    [Column("occurred_at")]
    public DateTime OccurredAt { get; set; }

    [Column("player_id")]
    public int PlayerId { get; set; }

    [Column("kind")]
    [MaxLength(16)]
    public required string Kind { get; set; }

    [Column("message")]
    [MaxLength(1000)]
    public required string Message { get; set; }

    /// <summary>The stack as the browser wrote it, unresolved: a source map turns it into source lines.</summary>
    [Column("stack", TypeName = "mediumtext")]
    public string? Stack { get; set; }

    /// <summary>React's component stack, for an error a view threw while drawing.</summary>
    [Column("component_stack", TypeName = "text")]
    public string? ComponentStack { get; set; }

    /// <summary>Where the browser said it was thrown (<c>file:line:col</c>), when it said.</summary>
    [Column("source")]
    [MaxLength(512)]
    public string? Source { get; set; }

    /// <summary>The client's last actions before it (JSON: <c>[{t, c, m}]</c>).</summary>
    [Column("breadcrumbs", TypeName = "text")]
    public string? Breadcrumbs { get; set; }

    [Column("build")]
    [MaxLength(64)]
    public required string Build { get; set; }

    /// <summary>The hotel's <c>production.version</c> the client ran with.</summary>
    [Column("production")]
    [MaxLength(64)]
    public string? Production { get; set; }

    /// <summary>Browser and major version: <c>Chrome 141</c>.</summary>
    [Column("browser")]
    [MaxLength(64)]
    public required string Browser { get; set; }

    /// <summary>Operating system: <c>Windows</c>, <c>macOS</c>, <c>Android</c>.</summary>
    [Column("platform")]
    [MaxLength(64)]
    public required string Platform { get; set; }

    [Column("user_agent")]
    [MaxLength(512)]
    public string? UserAgent { get; set; }

    /// <summary>The client's page, without its query (which carries the login ticket).</summary>
    [Column("page")]
    [MaxLength(256)]
    public string? Page { get; set; }

    [Column("room_id")]
    public int? RoomId { get; set; }

    /// <summary><c>width x height @ pixel ratio</c>.</summary>
    [Column("viewport")]
    [MaxLength(32)]
    public string? Viewport { get; set; }

    /// <summary><c>webgpu</c> or <c>webgl</c>, when the client said.</summary>
    [Column("renderer")]
    [MaxLength(32)]
    public string? Renderer { get; set; }

    /// <summary>How long the client had been running, in milliseconds.</summary>
    [Column("uptime_ms")]
    public long UptimeMs { get; set; }

    /// <summary>What the renderer and the page were when the GPU was lost (<c>renderer</c> reports), in one line.</summary>
    [Column("gpu")]
    [MaxLength(1024)]
    public string? Gpu { get; set; }
}
