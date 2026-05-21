using TeleDisk.Tests.NbdProtocol;

namespace TeleDisk.Tests.NbdProtocol.TransmissionCommands;

public sealed class CacheCommandTests
{
    [Fact]
    public void UsesExpectedCode()
    {
        NbdProtocolCatalog.Cache.Code.Should().Be((ushort)5);
    }

    [Fact]
    public void ExposesExpectedDataFlowBehavior()
    {
        var command = NbdProtocolCatalog.Cache;
        command.Supported.Should().BeTrue();
        command.RequiresPayload.Should().Be(false);
    }
}
