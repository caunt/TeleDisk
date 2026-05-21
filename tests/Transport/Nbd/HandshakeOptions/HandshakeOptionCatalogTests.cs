using TeleDisk.Tests.NbdProtocol;

namespace TeleDisk.Tests.NbdProtocol.HandshakeOptions;

public sealed class HandshakeOptionCatalogTests
{
    public static TheoryData<uint, bool, bool, bool> BehaviorCases => new()
    {
        { 1u, false, false, false },
        { 2u, true, false, true },
        { 3u, true, false, false },
        { 4u, false, true, false },
        { 5u, false, false, false },
        { 6u, true, false, false },
        { 7u, true, false, false },
        { 8u, true, false, false },
        { 9u, true, false, false },
        { 10u, true, false, false },
        { 11u, false, true, false }
    };

    [Theory]
    [MemberData(nameof(BehaviorCases))]
    public void ExposesExpectedFlags(uint code, bool acked, bool experimental, bool negotiationEnds)
    {
        var option = GetOption(code);
        option.Acked.Should().Be(acked);
        option.Experimental.Should().Be(experimental);
        option.NegotiationEnds.Should().Be(negotiationEnds);
    }

    [Fact]
    public void UsesUniqueCodes()
    {
        var uniqueCodes = new HashSet<uint>
        {
            NbdProtocolCatalog.ExportName.Code,
            NbdProtocolCatalog.Abort.Code,
            NbdProtocolCatalog.List.Code,
            NbdProtocolCatalog.PeekExport.Code,
            NbdProtocolCatalog.StartTls.Code,
            NbdProtocolCatalog.Info.Code,
            NbdProtocolCatalog.Go.Code,
            NbdProtocolCatalog.StructuredReply.Code,
            NbdProtocolCatalog.ListMetaContext.Code,
            NbdProtocolCatalog.SetMetaContext.Code,
            NbdProtocolCatalog.ExtendedHeaders.Code
        };

        uniqueCodes.Should().HaveCount(11);
    }

    private static NbdProtocolCatalog.HandshakeOption GetOption(uint code) => code switch
    {
        1 => NbdProtocolCatalog.ExportName,
        2 => NbdProtocolCatalog.Abort,
        3 => NbdProtocolCatalog.List,
        4 => NbdProtocolCatalog.PeekExport,
        5 => NbdProtocolCatalog.StartTls,
        6 => NbdProtocolCatalog.Info,
        7 => NbdProtocolCatalog.Go,
        8 => NbdProtocolCatalog.StructuredReply,
        9 => NbdProtocolCatalog.ListMetaContext,
        10 => NbdProtocolCatalog.SetMetaContext,
        11 => NbdProtocolCatalog.ExtendedHeaders,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unsupported handshake option code")
    };
}
