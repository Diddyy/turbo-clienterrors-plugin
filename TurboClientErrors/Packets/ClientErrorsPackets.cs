namespace TurboClientErrors.Packets;

/// <summary>
/// The <c>clienterrors</c> extension (version 1): Turbo's plugin packet space, never Habbo's. A
/// client that was not granted the capability reports nothing.
/// </summary>
public static class ClientErrorsPackets
{
    public const string CAPABILITY = "clienterrors";

    /// <summary>client to server: <c>int length</c> + one error report as UTF-8 JSON.</summary>
    public const int REPORT = 21100;
}
