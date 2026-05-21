using TeleDisk.Tests.NbdProtocol;

namespace TeleDisk.Tests.NbdProtocol.HandshakeOptions;

public sealed class SetMetaContextOptionTests
{
    [Fact]
    public void UsesExpectedCode()
    {
        NbdProtocolCatalog.SetMetaContext.Code.Should().Be(10u);
    }

    [Fact]
    public void ExposesExpectedNegotiationBehavior()
    {
        var option = NbdProtocolCatalog.SetMetaContext;
        option.Supported.Should().Be(true);
        option.EntersTransmission.Should().Be(false);
    }
}
