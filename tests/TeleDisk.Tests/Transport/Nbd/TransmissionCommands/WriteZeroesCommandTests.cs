using TeleDisk.Tests.NbdProtocol;

namespace TeleDisk.Tests.NbdProtocol.TransmissionCommands;

public sealed class WriteZeroesCommandTests
{
    [Fact]
    public void UsesExpectedCode()
    {
        NbdProtocolCatalog.WriteZeroes.Code.Should().Be((ushort)6);
    }

    [Fact]
    public void ExposesExpectedDataFlowBehavior()
    {
        var command = NbdProtocolCatalog.WriteZeroes;
        command.Supported.Should().BeTrue();
        command.RequiresPayload.Should().Be(false);
    }
}
