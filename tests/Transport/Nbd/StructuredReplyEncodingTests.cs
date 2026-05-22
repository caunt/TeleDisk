using System.Buffers.Binary;
using TeleDisk.Transport.Nbd;

namespace TeleDisk.Tests.TransportProtocol.Nbd;

public sealed class StructuredReplyEncodingTests
{
    [Fact]
    public void BuildStructuredReadPayload_OffsetZero_LengthFive_WritesExpectedBytes()
    {
        var payload = NbdEndpoint.BuildStructuredReadPayload(0, "hello"u8);

        payload.Length.Should().Be(13);
        payload[..8].Should().Equal(new byte[8]);
        payload[8..].Should().Equal("hello"u8.ToArray());
    }

    [Fact]
    public void BuildStructuredReadPayload_OffsetTwentyKilobytes_LengthThousandTwentyFour_WritesExpectedBytes()
    {
        var data = Enumerable.Repeat((byte)0xA5, 1024).ToArray();
        var payload = NbdEndpoint.BuildStructuredReadPayload(20_480, data);

        payload.Length.Should().Be(1032);
        BinaryPrimitives.ReadUInt64BigEndian(payload).Should().Be(20_480UL);
        payload[8..].Should().Equal(data);
    }

    [Fact]
    public void BuildStructuredReplyBytes_BlockStatus_WritesExpectedHeaderAndPayload()
    {
        var handle = Enumerable.Range(1, 8).Select(static value => (byte)value).ToArray();
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(payload, 1);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), 4096);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8), 0);

        var reply = NbdEndpoint.BuildStructuredReplyBytes(handle, 5, 1, payload);

        BinaryPrimitives.ReadUInt32BigEndian(reply).Should().Be(0x668e33ef);
        BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(4)).Should().Be(1);
        BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(6)).Should().Be(5);
        reply[8..16].Should().Equal(handle);
        BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(16)).Should().Be(12);
        reply[20..].Should().Equal(payload);
    }

    [Fact]
    public void BuildStructuredErrorPayload_WritesExpectedBytes()
    {
        var payload = NbdEndpoint.BuildStructuredErrorPayload(22);

        payload.Length.Should().Be(6);
        BinaryPrimitives.ReadUInt32BigEndian(payload).Should().Be(22);
        BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(4)).Should().Be(0);
    }

    [Fact]
    public void ConnectionState_DefaultsToStructuredAndExtendedHeadersDisabled()
    {
        var state = new NbdConnectionState();

        state.StructuredRepliesEnabled.Should().BeFalse();
        state.BlockStatusContextEnabled.Should().BeFalse();
        state.ExtendedHeadersEnabled.Should().BeFalse();
    }
}
