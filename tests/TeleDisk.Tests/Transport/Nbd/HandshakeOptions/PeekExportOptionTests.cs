using TeleDisk.Tests.NbdProtocol;

namespace TeleDisk.Tests.NbdProtocol.HandshakeOptions;

public sealed class PeekExportOptionTests
{
    [Fact]
    public void UsesExpectedCode()
    {
        NbdProtocolCatalog.PeekExport.Code.Should().Be(4u);
    }

    [Fact]
    public void ExposesExpectedNegotiationBehavior()
    {
        var option = NbdProtocolCatalog.PeekExport;
        option.Supported.Should().Be(false);
        option.EntersTransmission.Should().Be(false);
    }
}
