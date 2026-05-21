using TeleDisk.Tests.NbdProtocol;

namespace TeleDisk.Tests.NbdProtocol.HandshakeOptions;

public sealed class StructuredReplyOptionTests
{
    [Fact]
    public void UsesExpectedCode()
    {
        NbdProtocolCatalog.StructuredReply.Code.Should().Be(8u);
    }

    [Fact]
    public void ExposesExpectedNegotiationBehavior()
    {
        var option = NbdProtocolCatalog.StructuredReply;
        option.Supported.Should().Be(true);
        option.EntersTransmission.Should().Be(false);
    }
}
