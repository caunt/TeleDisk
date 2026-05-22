using System.Buffers.Binary;
using System.Text;
using TeleDisk.Transport.Nbd;

namespace TeleDisk.Tests.TransportProtocol.Nbd;

public sealed class MetaContextNegotiationTests
{
    [Fact]
    public void GetBaseAllocationContextId_WithBaseAllocationSecond_ReturnsTwo()
    {
        var payload = BuildPayload("teledisk", ["x-test:one", "base:allocation"]);

        var contextId = NbdEndpoint.GetBaseAllocationContextId(payload);

        contextId.Should().Be(2u);
    }

    [Fact]
    public void GetBaseAllocationContextId_WithoutBaseAllocation_ReturnsNull()
    {
        var payload = BuildPayload("teledisk", ["x-test:one"]);

        var contextId = NbdEndpoint.GetBaseAllocationContextId(payload);

        contextId.Should().BeNull();
    }

    private static byte[] BuildPayload(string exportName, string[] contexts)
    {
        var exportNameBytes = Encoding.UTF8.GetBytes(exportName);
        var contextBytes = contexts.Select(Encoding.UTF8.GetBytes).ToArray();
        var totalLength = 8 + exportNameBytes.Length + contextBytes.Sum(static bytes => 4 + bytes.Length);
        var payload = new byte[totalLength];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)exportNameBytes.Length);
        exportNameBytes.CopyTo(payload.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4 + exportNameBytes.Length), (uint)contextBytes.Length);
        var offset = 8 + exportNameBytes.Length;
        foreach (var context in contextBytes)
        {
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset), (uint)context.Length);
            offset += 4;
            context.CopyTo(payload.AsSpan(offset));
            offset += context.Length;
        }

        return payload;
    }
}
