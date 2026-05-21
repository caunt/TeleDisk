namespace TeleDisk.Tests.NbdProtocol;

internal static class NbdProtocolCatalog
{
    internal sealed record HandshakeOption(uint Code, bool EntersTransmission, bool Acked, bool Supported, bool Experimental = false, bool NegotiationEnds = false);

    internal sealed record TransmissionCommand(ushort Code, bool RequiresPayload, bool RepliesWithPayload, bool Supported, bool Experimental = false, bool EndsSession = false);

    internal static HandshakeOption ExportName { get; } = new(1, true, false, true);
    internal static HandshakeOption Abort { get; } = new(2, false, true, true, NegotiationEnds: true);
    internal static HandshakeOption List { get; } = new(3, false, true, true);
    internal static HandshakeOption PeekExport { get; } = new(4, false, false, false, Experimental: true);
    internal static HandshakeOption StartTls { get; } = new(5, false, false, false);
    internal static HandshakeOption Info { get; } = new(6, false, true, true);
    internal static HandshakeOption Go { get; } = new(7, true, true, true);
    internal static HandshakeOption StructuredReply { get; } = new(8, false, true, true);
    internal static HandshakeOption ListMetaContext { get; } = new(9, false, true, true);
    internal static HandshakeOption SetMetaContext { get; } = new(10, false, true, true);
    internal static HandshakeOption ExtendedHeaders { get; } = new(11, false, false, false, Experimental: true);

    internal static TransmissionCommand Read { get; } = new(0, false, true, true);
    internal static TransmissionCommand Write { get; } = new(1, true, false, true);
    internal static TransmissionCommand Disconnect { get; } = new(2, false, false, true, EndsSession: true);
    internal static TransmissionCommand Flush { get; } = new(3, false, false, true);
    internal static TransmissionCommand Trim { get; } = new(4, false, false, true);
    internal static TransmissionCommand Cache { get; } = new(5, false, false, true);
    internal static TransmissionCommand WriteZeroes { get; } = new(6, false, false, true);
    internal static TransmissionCommand BlockStatus { get; } = new(7, false, true, true);
    internal static TransmissionCommand Resize { get; } = new(8, false, false, true, Experimental: true);
}
