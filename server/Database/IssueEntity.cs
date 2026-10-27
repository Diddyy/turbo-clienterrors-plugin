using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TurboClientErrors.Database;

/// <summary>
/// One client error, however often and on whichever builds it happened: the reports whose
/// fingerprints (<see cref="FingerprintEntity"/>) are its, with their totals.
/// </summary>
[Table("issues")]
[Index(nameof(Signature))]
[Index(nameof(LastSeen))]
[Index(nameof(Status), nameof(LastSeen))]
public class IssueEntity
{
    public const string OPEN = "open";
    public const string RESOLVED = "resolved";
    public const string IGNORED = "ignored";

    [Key]
    [Column("id")]
    public int Id { get; set; }

    /// <summary>
    /// The error with its build and line numbers left out (<c>Fingerprints.Signature</c>): the same
    /// error on a later build finds its issue by it.
    /// </summary>
    [Column("signature")]
    [MaxLength(40)]
    public required string Signature { get; set; }

    /// <summary><c>error</c>, <c>rejection</c>, <c>react</c>, <c>renderer</c> or <c>logged</c>: how the first report was caught.</summary>
    [Column("kind")]
    [MaxLength(16)]
    public required string Kind { get; set; }

    /// <summary>The first report's message, as the client wrote it (cut).</summary>
    [Column("message")]
    [MaxLength(512)]
    public required string Message { get; set; }

    /// <summary>The first report's top stack frame: where it was thrown.</summary>
    [Column("top_frame")]
    [MaxLength(512)]
    public string? TopFrame { get; set; }

    /// <summary><see cref="OPEN"/>, <see cref="RESOLVED"/> or <see cref="IGNORED"/>.</summary>
    [Column("status")]
    [MaxLength(16)]
    public required string Status { get; set; }

    [Column("occurrences")]
    public int Occurrences { get; set; }

    /// <summary>How many players reported it.</summary>
    [Column("players")]
    public int Players { get; set; }

    [Column("first_seen")]
    public DateTime FirstSeen { get; set; }

    [Column("last_seen")]
    public DateTime LastSeen { get; set; }

    [Column("last_build")]
    [MaxLength(64)]
    public string? LastBuild { get; set; }

    /// <summary>When staff resolved or ignored it.</summary>
    [Column("resolved_at")]
    public DateTime? ResolvedAt { get; set; }

    /// <summary>The staff member who resolved or ignored it.</summary>
    [Column("resolved_by")]
    public int? ResolvedBy { get; set; }

    /// <summary>When a resolved issue came back on a build it had not been seen on.</summary>
    [Column("reopened_at")]
    public DateTime? ReopenedAt { get; set; }

    [Column("reopened_build")]
    [MaxLength(64)]
    public string? ReopenedBuild { get; set; }

    /// <summary>The tester report it belongs to: a number, a link, any text staff put there.</summary>
    [Column("report")]
    [MaxLength(512)]
    public string? Report { get; set; }

    [Column("note")]
    [MaxLength(1000)]
    public string? Note { get; set; }
}
