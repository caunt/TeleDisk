using TeleDisk.Tests.NbdProtocol;

namespace TeleDisk.Tests.NbdProtocol.HandshakeOptions;

public sealed class GoOptionTests
{
    [Fact]
    public void UsesExpectedCode()
    {
        NbdProtocolCatalog.Go.Code.Should().Be(7u);
    }

    [Fact]
    public void ExposesExpectedNegotiationBehavior()
    {
        var option = NbdProtocolCatalog.Go;
        option.Supported.Should().Be(true);
        option.EntersTransmission.Should().Be(true);
    }
}
