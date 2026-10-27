using Turbo.Primitives.Networking;
using Turbo.Primitives.Packets;

namespace TurboClientErrors.Packets;

internal sealed class ClientErrorReportParser : IParser
{
    /// <summary>A length the packet does not hold is read as nothing, never past its end.</summary>
    public IMessageEvent Parse(IClientPacket packet)
    {
        var length = packet.Remaining >= 4 ? packet.PopInt() : 0;

        return new ClientErrorReportMessage
        {
            Report = length > 0 && length <= packet.Remaining ? packet.PopBytes(length) : [],
        };
    }
}
