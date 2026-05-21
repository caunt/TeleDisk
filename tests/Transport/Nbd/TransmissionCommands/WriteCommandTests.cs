using TeleDisk.Tests.NbdProtocol;

namespace TeleDisk.Tests.NbdProtocol.TransmissionCommands;

public sealed class WriteCommandTests
{
    [Fact]
    public void UsesExpectedCode()
    {
        NbdProtocolCatalog.Write.Code.Should().Be((ushort)1);
    }

    [Fact]
    public void ExposesExpectedDataFlowBehavior()
    {
        var command = NbdProtocolCatalog.Write;
        command.Supported.Should().BeTrue();
        command.RequiresPayload.Should().Be(true);
    }
}
