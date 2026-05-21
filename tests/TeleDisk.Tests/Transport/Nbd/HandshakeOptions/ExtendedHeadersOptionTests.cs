using TeleDisk.Tests.NbdProtocol;

namespace TeleDisk.Tests.NbdProtocol.HandshakeOptions;

public sealed class ExtendedHeadersOptionTests
{
    [Fact]
    public void UsesExpectedCode()
    {
        NbdProtocolCatalog.ExtendedHeaders.Code.Should().Be(11u);
    }

    [Fact]
    public void ExposesExpectedNegotiationBehavior()
    {
        var option = NbdProtocolCatalog.ExtendedHeaders;
        option.Supported.Should().Be(false);
        option.EntersTransmission.Should().Be(false);
    }
}
