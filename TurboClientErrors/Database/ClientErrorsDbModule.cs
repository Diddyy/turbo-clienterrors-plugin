using System;
using System.Threading;
using System.Threading.Tasks;
using Turbo.Contracts.Plugins;
using Turbo.Database.Migrations;

namespace TurboClientErrors.Database;

public class ClientErrorsDbModule : IPluginDbModule
{
    public Task MigrateAsync(IServiceProvider sp, CancellationToken ct) =>
        MigrationHelper.MigrateAsync<ClientErrorsDbContext>(sp, ct);

    public Task UninstallAsync(IServiceProvider sp, CancellationToken ct) =>
        MigrationHelper.UninstallAsync<ClientErrorsDbContext>(sp, ct);
}
