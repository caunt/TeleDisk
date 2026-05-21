using TeleDisk.Tests.NbdProtocol;

namespace TeleDisk.Tests.NbdProtocol.HandshakeOptions;

public sealed class ListOptionTests
{
    [Fact]
    public void UsesExpectedCode()
    {
        NbdProtocolCatalog.List.Code.Should().Be(3u);
    }

    [Fact]
    public void ExposesExpectedNegotiationBehavior()
    {
        var option = NbdProtocolCatalog.List;
        option.Supported.Should().Be(true);
        option.EntersTransmission.Should().Be(false);
    }
}
