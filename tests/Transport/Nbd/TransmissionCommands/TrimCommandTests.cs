using TeleDisk.Tests.NbdProtocol;

namespace TeleDisk.Tests.NbdProtocol.TransmissionCommands;

public sealed class TrimCommandTests
{
    [Fact]
    public void UsesExpectedCode()
    {
        NbdProtocolCatalog.Trim.Code.Should().Be((ushort)4);
    }

    [Fact]
    public void ExposesExpectedDataFlowBehavior()
    {
        var command = NbdProtocolCatalog.Trim;
        command.Supported.Should().BeTrue();
        command.RequiresPayload.Should().Be(false);
    }
}
