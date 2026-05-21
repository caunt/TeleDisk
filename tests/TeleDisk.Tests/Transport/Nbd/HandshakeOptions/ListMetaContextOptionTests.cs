using TeleDisk.Tests.NbdProtocol;

namespace TeleDisk.Tests.NbdProtocol.HandshakeOptions;

public sealed class ListMetaContextOptionTests
{
    [Fact]
    public void UsesExpectedCode()
    {
        NbdProtocolCatalog.ListMetaContext.Code.Should().Be(9u);
    }

    [Fact]
    public void ExposesExpectedNegotiationBehavior()
    {
        var option = NbdProtocolCatalog.ListMetaContext;
        option.Supported.Should().Be(true);
        option.EntersTransmission.Should().Be(false);
    }
}
