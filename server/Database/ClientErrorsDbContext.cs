using Microsoft.EntityFrameworkCore;
using Turbo.Database.Context;
using Turbo.Database.Delegates;

namespace TurboClientErrors.Database;

/// <summary>
/// The plugin's tables, under its prefix (<c>clienterrors_</c>). No foreign keys into core: the
/// players and rooms a report names may be gone.
/// </summary>
public class ClientErrorsDbContext(
    DbContextOptions<ClientErrorsDbContext> options,
    TablePrefixProvider prefix
) : PluginDbContextBase<ClientErrorsDbContext>(options, prefix)
{
    public DbSet<IssueEntity> Issues => Set<IssueEntity>();

    public DbSet<FingerprintEntity> Fingerprints => Set<FingerprintEntity>();

    public DbSet<OccurrenceEntity> Occurrences => Set<OccurrenceEntity>();

    public DbSet<FacetEntity> Facets => Set<FacetEntity>();

    public DbSet<IssueDayEntity> Days => Set<IssueDayEntity>();
}
