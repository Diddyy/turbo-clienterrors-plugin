using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TurboClientErrors.Database;

/// <summary>How often an issue was reported on one UTC day: its trend.</summary>
[Table("issue_days")]
[Index(nameof(IssueId), nameof(Day), IsUnique = true)]
[Index(nameof(Day))]
public class IssueDayEntity
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("issue_id")]
    public int IssueId { get; set; }

    /// <summary>The day, at midnight UTC.</summary>
    [Column("day")]
    public DateTime Day { get; set; }

    [Column("occurrences")]
    public int Occurrences { get; set; }
}
