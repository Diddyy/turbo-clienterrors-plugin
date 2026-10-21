using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Turbo.Database.Delegates;
using TurboClientErrors.Database;
using TurboClientErrors.Ingest;

namespace TurboClientErrors.Tests;

internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 21, 3, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class Settings(ClientErrorsConfig value) : IOptionsMonitor<ClientErrorsConfig>
{
    public ClientErrorsConfig Value { get; set; } = value;

    public ClientErrorsConfig CurrentValue => Value;

    public ClientErrorsConfig Get(string? name) => Value;

    public IDisposable? OnChange(Action<ClientErrorsConfig, string?> listener) => null;
}

/// <summary>
/// The plugin's services over an in-memory SQLite database (the real SQL: transactions, unique
/// keys, bulk deletes), with a clock the test moves.
/// </summary>
internal sealed class TestHost : IDisposable
{
    public static readonly CancellationToken CT = CancellationToken.None;

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public TestHost(ClientErrorsConfig? config = null)
    {
        _connection.Open();
        Settings = new Settings(config ?? new ClientErrorsConfig());

        var services = new ServiceCollection();

        services.AddSingleton<TablePrefixProvider>(() => "clienterrors_");
        services.AddDbContext<ClientErrorsDbContext>(o => o.UseSqlite(_connection));
        Services = services.BuildServiceProvider();

        using (var scope = Services.CreateScope())
            scope
                .ServiceProvider.GetRequiredService<ClientErrorsDbContext>()
                .Database.EnsureCreated();

        var scopes = Services.GetRequiredService<IServiceScopeFactory>();

        Ingest = new ClientErrorIngest(Settings, Time);
        Store = new ClientErrorStore(scopes, Settings, NullLogger<ClientErrorStore>.Instance);
        Retention = new ClientErrorRetention(scopes, Settings, Time);
    }

    public ManualTime Time { get; } = new();

    public Settings Settings { get; }

    public ServiceProvider Services { get; }

    public ClientErrorIngest Ingest { get; }

    public ClientErrorStore Store { get; }

    public ClientErrorRetention Retention { get; }

    public T Query<T>(Func<ClientErrorsDbContext, T> query)
    {
        using var scope = Services.CreateScope();

        return query(scope.ServiceProvider.GetRequiredService<ClientErrorsDbContext>());
    }

    /// <summary>Stores reports as if each came from <paramref name="player"/> now.</summary>
    public Task<StoreResult> StoreAsync(int player, params string[] reports) =>
        Store.StoreAsync(
            reports
                .Select(x => new PendingReport(
                    player,
                    Time.Now.UtcDateTime,
                    Encoding.UTF8.GetBytes(x)
                ))
                .ToList(),
            CT
        );

    public void Dispose()
    {
        Services.Dispose();
        _connection.Dispose();
    }

    /// <summary>A report as nitro-next sends one.</summary>
    public static string Report(
        string message = "Cannot read properties of undefined (reading 'roomId')",
        string build = "a1b2c3d4e5f6",
        string? stack = null,
        string kind = "error",
        string userAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36",
        object[]? breadcrumbs = null,
        string page = "https://hotel.example/client?sso=secret-ticket"
    ) =>
        JsonSerializer.Serialize(
            new
            {
                v = 1,
                kind,
                name = "TypeError",
                message,
                stack = stack
                    ?? $"TypeError: {message}\n    at RoomView (https://hotel.example/assets/index-B3x9_kQa.js:12:3456)\n    at renderWithHooks (https://hotel.example/assets/vendor-Ck2pL0aa.js:1:999)",
                source = "https://hotel.example/assets/index-B3x9_kQa.js:12:3456",
                build,
                production = "PRODUCTION-202610201032",
                userAgent,
                platform = "Win32",
                page,
                roomId = 7,
                viewport = new
                {
                    width = 1920,
                    height = 1080,
                    pixelRatio = 1.25,
                },
                renderer = "webgpu",
                uptimeMs = 65000,
                breadcrumbs = breadcrumbs
                    ??
                    [
                        new
                        {
                            t = 60000,
                            c = "out",
                            m = "OpenFlatConnection",
                        },
                        new
                        {
                            t = 64000,
                            c = "out",
                            m = "GetCatalogPage",
                        },
                    ],
            }
        );
}
