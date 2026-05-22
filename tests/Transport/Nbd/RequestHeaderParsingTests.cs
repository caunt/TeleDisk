using System.Buffers.Binary;
using TeleDisk.Transport.Nbd;

namespace TeleDisk.Tests.TransportProtocol.Nbd;

public sealed class RequestHeaderParsingTests
{
    [Fact]
    public void TryParseRequestHeader_ClassicHeader_ParsesExpectedValues()
    {
        var request = new byte[28];
        BinaryPrimitives.WriteUInt32BigEndian(request, 0x25609513);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6), 0);
        Enumerable.Range(1, 8).Select(static value => (byte)value).ToArray().CopyTo(request, 8);
        BinaryPrimitives.WriteUInt64BigEndian(request.AsSpan(16), 4096);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(24), 512);

        var parsed = NbdEndpoint.TryParseRequestHeader(request, false, out var header);

        parsed.Should().BeTrue();
        header.Flags.Should().Be(1);
        header.Command.Should().Be(NbdCommand.Read);
        header.Offset.Should().Be(4096);
        header.Length.Should().Be(512);
    }

    [Fact]
    public void TryParseRequestHeader_ExtendedHeader_ParsesExpectedValues()
    {
        var request = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(request, 0x21e41c71);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6), 7);
        Enumerable.Range(1, 8).Select(static value => (byte)(value + 10)).ToArray().CopyTo(request, 8);
        BinaryPrimitives.WriteUInt64BigEndian(request.AsSpan(16), 8192);
        BinaryPrimitives.WriteUInt64BigEndian(request.AsSpan(24), 1024);

        var parsed = NbdEndpoint.TryParseRequestHeader(request, true, out var header);

        parsed.Should().BeTrue();
        header.Flags.Should().Be(2);
        header.Command.Should().Be(NbdCommand.BlockStatus);
        header.Offset.Should().Be(8192);
        header.Length.Should().Be(1024);
    }
}
