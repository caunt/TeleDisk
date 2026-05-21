using TeleDisk.Tests.NbdProtocol;

namespace TeleDisk.Tests.NbdProtocol.TransmissionCommands;

public sealed class BlockStatusCommandTests
{
    [Fact]
    public void UsesExpectedCode()
    {
        NbdProtocolCatalog.BlockStatus.Code.Should().Be((ushort)7);
    }

    [Fact]
    public void ExposesExpectedDataFlowBehavior()
    {
        var command = NbdProtocolCatalog.BlockStatus;
        command.Supported.Should().BeTrue();
        command.RequiresPayload.Should().Be(false);
    }
}
