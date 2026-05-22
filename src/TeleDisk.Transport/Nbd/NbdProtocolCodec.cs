using System.Buffers.Binary;
using System.Text;

namespace TeleDisk.Transport.Nbd;

internal static class NbdProtocolCodec
{
    private const uint NbdRequestMagic = 0x25609513;
    private const uint NbdRequestExtendedMagic = 0x21e41c71;
    private const uint NbdReplyMagic = 0x67446698;
    private const uint NbdStructuredReplyMagic = 0x668e33ef;
    private const uint NbdExtendedStructuredReplyMagic = 0x6e8a278c;
    private const int NbdRequestBytes = 28;
    private const int NbdExtendedRequestBytes = 32;
    private const int NbdReplyBytes = 16;
    private const int NbdStructuredReplyHeaderBytes = 20;
    private const int NbdExtendedStructuredReplyHeaderBytes = 32;

    internal static byte[] BuildStructuredReplyBytes(ReadOnlySpan<byte> handle, ushort replyType, ushort flags, ReadOnlySpan<byte> payload, bool useExtendedHeaders = false, ulong offset = 0)
    {
        var headerLength = useExtendedHeaders ? NbdExtendedStructuredReplyHeaderBytes : NbdStructuredReplyHeaderBytes;
        var bytes = new byte[headerLength + payload.Length];
        var header = bytes.AsSpan(0, headerLength);
        BinaryPrimitives.WriteUInt32BigEndian(header, useExtendedHeaders ? NbdExtendedStructuredReplyMagic : NbdStructuredReplyMagic);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], flags);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], replyType);
        handle.CopyTo(header[8..]);
        if (useExtendedHeaders)
        {
            BinaryPrimitives.WriteUInt64BigEndian(header[16..], offset);
            BinaryPrimitives.WriteUInt64BigEndian(header[24..], checked((ulong)payload.Length));
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(header[16..], checked((uint)payload.Length));
        }

        payload.CopyTo(bytes.AsSpan(headerLength));
        return bytes;
    }

    internal static byte[] BuildStructuredReadPayload(long offset, ReadOnlySpan<byte> payload)
    {
        var responsePayload = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt64BigEndian(responsePayload, checked((ulong)offset));
        payload.CopyTo(responsePayload.AsSpan(8));
        return responsePayload;
    }

    internal static byte[] BuildStructuredErrorPayload(uint error)
    {
        var payload = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(payload, error);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4), 0);
        return payload;
    }

    internal static byte[] BuildSimpleReplyBytes(ReadOnlySpan<byte> handle, uint error)
    {
        var bytes = new byte[NbdReplyBytes];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, NbdReplyMagic);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], error);
        handle.CopyTo(span[8..]);
        return bytes;
    }

    internal static bool TryParseRequestHeader(ReadOnlySpan<byte> requestBytes, bool useExtendedHeaders, out NbdRequestHeader requestHeader)
    {
        requestHeader = default;
        if (requestBytes.Length != (useExtendedHeaders ? NbdExtendedRequestBytes : NbdRequestBytes))
        {
            return false;
        }

        var expectedMagic = useExtendedHeaders ? NbdRequestExtendedMagic : NbdRequestMagic;
        if (BinaryPrimitives.ReadUInt32BigEndian(requestBytes) != expectedMagic)
        {
            return false;
        }

        requestHeader = new NbdRequestHeader(
            BinaryPrimitives.ReadUInt16BigEndian(requestBytes[4..]),
            (NbdCommand)BinaryPrimitives.ReadUInt16BigEndian(requestBytes[6..]),
            requestBytes.Slice(8, 8).ToArray(),
            BinaryPrimitives.ReadUInt64BigEndian(requestBytes[16..]),
            useExtendedHeaders ? BinaryPrimitives.ReadUInt64BigEndian(requestBytes[24..]) : BinaryPrimitives.ReadUInt32BigEndian(requestBytes[24..]));
        return true;
    }

    internal static uint? GetBaseAllocationContextId(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
        {
            return null;
        }

        var exportNameLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload));
        if (payload.Length < 8 + exportNameLength)
        {
            return null;
        }

        var contextCount = checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload[(4 + exportNameLength)..]));
        var offset = 8 + exportNameLength;
        uint nextContextId = 1;
        for (var contextIndex = 0; contextIndex < contextCount; contextIndex++)
        {
            if (payload.Length < offset + 4)
            {
                return null;
            }

            var contextLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload[offset..]));
            offset += 4;
            if (payload.Length < offset + contextLength)
            {
                return null;
            }

            if (contextLength == 15 && Encoding.UTF8.GetString(payload.Slice(offset, contextLength)) == "base:allocation")
            {
                return nextContextId;
            }

            nextContextId++;
            offset += contextLength;
        }

        return null;
    }
}
