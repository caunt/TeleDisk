using TeleDisk.Transport.Nbd;

namespace TeleDisk.Tests.NbdProtocol.TransmissionCommands;

public sealed class TransmissionCommandCatalogTests
{
    public static TheoryData<ushort, bool, bool, bool> Cases => new()
    {
        { 0, false, true, false },
        { 1, true, false, false },
        { 2, false, false, true },
        { 3, false, false, false },
        { 4, false, false, false },
        { 5, false, false, false },
        { 6, false, false, false },
        { 7, false, true, false },
        { 8, false, false, false }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void UsesExpectedCodeAndWireBehavior(ushort code, bool requiresPayload, bool repliesWithPayload, bool endsSession)
    {
        Enum.IsDefined(typeof(NbdCommand), code).Should().BeTrue();
        var command = (NbdCommand)code;
        requiresPayload.Should().Be(command is NbdCommand.Write);
        repliesWithPayload.Should().Be(command is NbdCommand.Read or NbdCommand.BlockStatus);
        endsSession.Should().Be(command is NbdCommand.Disconnect);
    }

    [Fact]
    public void UsesContiguousCommandRangeRequiredByNbdClients()
    {
        Enum.GetValues<NbdCommand>()
            .Select(static command => (ushort)command)
            .Should().Equal([0, 1, 2, 3, 4, 5, 6, 7, 8]);
    }
}
