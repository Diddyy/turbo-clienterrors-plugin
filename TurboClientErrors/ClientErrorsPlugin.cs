using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Turbo.Contracts.Plugins;
using Turbo.Contracts.Plugins.Admin;
using Turbo.Contracts.Plugins.Exports;
using Turbo.Database.Extensions;
using Turbo.Primitives.Networking.Extensions;
using TurboClientErrors.Database;
using TurboClientErrors.Ingest;
using TurboClientErrors.Packets;

namespace TurboClientErrors;

/// <summary>
/// The errors players' clients hit, grouped into issues: what was thrown, how often, on which builds
/// and browsers, and what the client did just before, kept in <c>clienterrors_*</c> and shown on the
/// admin panel's Client errors page. The client half is the <c>clienterrors</c> extension
/// (<see cref="ClientErrorsPackets"/>): a client reports only once the server accepted it.
/// </summary>
public sealed class ClientErrorsPlugin : ITurboPlugin
{
    private readonly List<IDisposable> _registrations = [];
    private readonly Lock _capabilityLock = new();
    private IExtensionPacketRegistry? _registry;
    private IDisposable? _capability;
    private IDisposable? _settingsListener;
    private ClientErrorsWorker? _worker;

    public string Key => "clienterrors";

    public string Version => "1.0.0";

    public void ConfigureServices(IServiceCollection services, PluginManifest manifest)
    {
        services.AddPluginDatabaseContext<ClientErrorsDbContext, ClientErrorsDbModule>();
        // Bound as a host module binds its options: the panel lists them, and saves reach the monitor.
        services
            .AddOptions<ClientErrorsConfig>()
            .BindConfiguration(ClientErrorsConfig.SECTION_NAME);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ClientErrorIngest>();
        services.AddSingleton<ClientErrorStore>();
        services.AddSingleton<ClientErrorRetention>();
        services.AddSingleton<ClientErrorsWorker>();
        services.AddSingleton<IPluginAdmin, ClientErrorsAdmin>();
    }

    public Task StartAsync(IServiceProvider services, CancellationToken ct)
    {
        _registry = services
            .GetRequiredService<IHostServices>()
            .GetRequiredService<IExtensionPacketRegistry>();
        _registrations.Add(
            _registry.RegisterParser(ClientErrorsPackets.REPORT, new ClientErrorReportParser())
        );

        // The capability follows Enabled: a client that logs in while it is off is never offered it.
        var settings = services.GetRequiredService<IOptionsMonitor<ClientErrorsConfig>>();

        OfferCapability(settings.CurrentValue.Enabled);
        _settingsListener = settings.OnChange(x => OfferCapability(x.Enabled));

        _worker = services.GetRequiredService<ClientErrorsWorker>();
        _worker.Start();

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _settingsListener?.Dispose();
        _settingsListener = null;
        OfferCapability(false);

        foreach (var registration in _registrations)
            registration.Dispose();

        _registrations.Clear();

        if (_worker is not null)
            await _worker.StopAsync().ConfigureAwait(false);

        _worker = null;
        _registry = null;
    }

    private void OfferCapability(bool offer)
    {
        lock (_capabilityLock)
        {
            if (offer && _capability is null && _registry is not null)
                _capability = _registry.RegisterCapability(ClientErrorsPackets.CAPABILITY);
            else if (!offer && _capability is not null)
            {
                _capability.Dispose();
                _capability = null;
            }
        }
    }

    public Task BindExportsAsync(IExportBinder binder, IServiceProvider services) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
