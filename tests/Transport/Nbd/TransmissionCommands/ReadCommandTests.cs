using TeleDisk.Tests.NbdProtocol;

namespace TeleDisk.Tests.NbdProtocol.TransmissionCommands;

public sealed class ReadCommandTests
{
    [Fact]
    public void UsesExpectedCode()
    {
        NbdProtocolCatalog.Read.Code.Should().Be((ushort)0);
    }

    [Fact]
    public void ExposesExpectedDataFlowBehavior()
    {
        var command = NbdProtocolCatalog.Read;
        command.Supported.Should().BeTrue();
        command.RequiresPayload.Should().Be(false);
    }
}
