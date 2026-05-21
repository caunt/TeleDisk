using TeleDisk.Tests.NbdProtocol;

namespace TeleDisk.Tests.NbdProtocol.TransmissionCommands;

public sealed class DisconnectCommandTests
{
    [Fact]
    public void UsesExpectedCode()
    {
        NbdProtocolCatalog.Disconnect.Code.Should().Be((ushort)2);
    }

    [Fact]
    public void ExposesExpectedDataFlowBehavior()
    {
        var command = NbdProtocolCatalog.Disconnect;
        command.Supported.Should().BeTrue();
        command.RequiresPayload.Should().Be(false);
    }
}
