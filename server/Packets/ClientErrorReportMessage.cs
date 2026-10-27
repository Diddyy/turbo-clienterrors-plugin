using Turbo.Primitives.Networking;

namespace TurboClientErrors.Packets;

/// <summary>One error a client caught, as UTF-8 JSON (see the README for its fields).</summary>
public sealed record ClientErrorReportMessage : IMessageEvent
{
    public required byte[] Report { get; init; }
}
