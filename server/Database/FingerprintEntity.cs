using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TurboClientErrors.Database;

/// <summary>
/// A fingerprint (normalised message, top frames and build: <c>Fingerprints.Of</c>) and the issue
/// its reports count towards. An issue seen on several builds has one per build.
/// </summary>
[Table("fingerprints")]
[Index(nameof(Fingerprint), IsUnique = true)]
[Index(nameof(IssueId))]
public class FingerprintEntity
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("fingerprint")]
    [MaxLength(40)]
    public required string Fingerprint { get; set; }

    [Column("issue_id")]
    public int IssueId { get; set; }

    [Column("build")]
    [MaxLength(64)]
    public required string Build { get; set; }
}
