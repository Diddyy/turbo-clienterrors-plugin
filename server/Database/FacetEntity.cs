using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TurboClientErrors.Database;

/// <summary>
/// How often an issue was reported on one build, browser, platform or by one player, kept apart
/// from the occurrences so the figures stay whole when occurrences expire.
/// </summary>
[Table("facets")]
[Index(nameof(IssueId), nameof(Facet), nameof(Value), IsUnique = true)]
public class FacetEntity
{
    public const string BUILD = "build";
    public const string BROWSER = "browser";
    public const string PLATFORM = "platform";
    public const string PLAYER = "player";

    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("issue_id")]
    public int IssueId { get; set; }

    /// <summary><see cref="BUILD"/>, <see cref="BROWSER"/>, <see cref="PLATFORM"/> or <see cref="PLAYER"/>.</summary>
    [Column("facet")]
    [MaxLength(16)]
    public required string Facet { get; set; }

    [Column("value")]
    [MaxLength(64)]
    public required string Value { get; set; }

    [Column("occurrences")]
    public int Occurrences { get; set; }

    [Column("first_seen")]
    public DateTime FirstSeen { get; set; }

    [Column("last_seen")]
    public DateTime LastSeen { get; set; }
}
