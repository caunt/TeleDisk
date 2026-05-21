using TeleDisk.Tests.NbdProtocol;

namespace TeleDisk.Tests.NbdProtocol.TransmissionCommands;

public sealed class TransmissionCommandCatalogTests
{
    public static TheoryData<ushort, bool, bool, bool> BehaviorCases => new()
    {
        { 0, true, false, false },
        { 1, false, false, false },
        { 2, false, false, true },
        { 3, false, false, false },
        { 4, false, false, false },
        { 5, false, false, false },
        { 6, false, false, false },
        { 7, true, false, false },
        { 8, false, true, false }
    };

    [Theory]
    [MemberData(nameof(BehaviorCases))]
    public void ExposesExpectedFlags(ushort code, bool repliesWithPayload, bool experimental, bool endsSession)
    {
        var command = GetCommand(code);
        command.RepliesWithPayload.Should().Be(repliesWithPayload);
        command.Experimental.Should().Be(experimental);
        command.EndsSession.Should().Be(endsSession);
    }

    [Fact]
    public void UsesUniqueCodes()
    {
        var uniqueCodes = new HashSet<ushort>
        {
            NbdProtocolCatalog.Read.Code,
            NbdProtocolCatalog.Write.Code,
            NbdProtocolCatalog.Disconnect.Code,
            NbdProtocolCatalog.Flush.Code,
            NbdProtocolCatalog.Trim.Code,
            NbdProtocolCatalog.Cache.Code,
            NbdProtocolCatalog.WriteZeroes.Code,
            NbdProtocolCatalog.BlockStatus.Code,
            NbdProtocolCatalog.Resize.Code
        };

        uniqueCodes.Should().HaveCount(9);
    }

    private static NbdProtocolCatalog.TransmissionCommand GetCommand(ushort code) => code switch
    {
        0 => NbdProtocolCatalog.Read,
        1 => NbdProtocolCatalog.Write,
        2 => NbdProtocolCatalog.Disconnect,
        3 => NbdProtocolCatalog.Flush,
        4 => NbdProtocolCatalog.Trim,
        5 => NbdProtocolCatalog.Cache,
        6 => NbdProtocolCatalog.WriteZeroes,
        7 => NbdProtocolCatalog.BlockStatus,
        8 => NbdProtocolCatalog.Resize,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unsupported transmission command code")
    };
}
