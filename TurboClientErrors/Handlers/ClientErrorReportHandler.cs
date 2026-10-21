using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Turbo.Contracts.Plugins;
using Turbo.Messages.Registry;
using Turbo.Primitives.Orleans;
using TurboClientErrors.Ingest;
using TurboClientErrors.Packets;

namespace TurboClientErrors.Handlers;

/// <summary>
/// A client's error report: queued when its session accepted the <c>clienterrors</c> capability and
/// is within its limits (<see cref="ClientErrorIngest"/>), dropped otherwise. Never waits for the
/// database.
/// </summary>
public sealed class ClientErrorReportHandler(ClientErrorIngest ingest, IHostServices host)
    : IMessageHandler<ClientErrorReportMessage>
{
    public async ValueTask HandleAsync(
        ClientErrorReportMessage message,
        MessageContext ctx,
        CancellationToken ct
    )
    {
        if (ctx.PlayerId <= 0)
            return;

        await ingest
            .AcceptAsync(
                ctx.SessionKey,
                ctx.PlayerId,
                message.Report,
                async token =>
                    await host.GetRequiredService<IGrainFactory>()
                        .SessionAcceptsCapabilityAsync(
                            ctx.PlayerId,
                            ctx.SessionKey,
                            ClientErrorsPackets.CAPABILITY,
                            token
                        )
                        .ConfigureAwait(false),
                ct
            )
            .ConfigureAwait(false);
    }
}
