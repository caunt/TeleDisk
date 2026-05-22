using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using TeleDisk.Domain.Storage;

namespace TeleDisk.Transport.Nbd;

internal sealed class NbdHandshakeNegotiator(ILogger<NbdEndpoint> logger, Func<long> getExportSizeBytes)
{
    private const ulong NbdMagic = 0x4e42444d41474943;
    private const ulong NbdOptionMagic = 0x49484156454F5054;
    private const ulong NbdOptionReplyMagic = 0x3e889045565a9;
    private const uint NbdFlagHasFlags = 1;
    private const uint NbdFlagSendFlush = 1 << 2;
    private const uint NbdFlagSendTrim = 1 << 5;
    private const uint NbdFlagSendWriteZeroes = 1 << 6;
    private const uint NbdFlagSendCache = 1 << 10;
    private const uint NbdFlagSendFastZero = 1 << 11;
    private const uint NbdFlagSendBlockStatus = 1 << 12;
    private const uint NbdFlagCanResize = 1 << 13;
    private const ushort NbdHandshakeFlagFixedNewStyle = 1;
    private const ushort NbdHandshakeFlagNoZeroes = 1 << 1;
    private const uint NbdOptionExportName = 1;
    private const uint NbdOptionAbort = 2;
    private const uint NbdOptionList = 3;
    private const uint NbdOptionInfo = 6;
    private const uint NbdOptionGo = 7;
    private const uint NbdOptionStructuredReply = 8;
    private const uint NbdOptionListMetaContext = 9;
    private const uint NbdOptionSetMetaContext = 10;
    private const uint NbdOptionExtendedHeaders = 11;
    private const uint NbdReplyTypeAck = 1;
    private const uint NbdReplyTypeServer = 2;
    private const uint NbdReplyTypeInfo = 3;
    private const uint NbdReplyTypeMetaContext = 4;
    private const uint NbdReplyTypeErrUnsupported = 0x80000001;
    private const ushort NbdInfoExport = 0;
    private const ushort NbdInfoName = 1;
    private const ushort NbdInfoDescription = 2;
    private const ushort NbdInfoBlockSize = 3;
    private const uint NbdMetaContextBaseAllocation = 1;
    private const int NbdOptionHeaderBytes = 16;
    private const int NbdOptionReplyHeaderBytes = 20;

    internal async Task<NbdConnectionState> NegotiateAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        _ = logger;
        var handshakeSpan = (stackalloc byte[18]);
        BinaryPrimitives.WriteUInt64BigEndian(handshakeSpan, NbdMagic);
        BinaryPrimitives.WriteUInt64BigEndian(handshakeSpan[8..], NbdOptionMagic);
        BinaryPrimitives.WriteUInt16BigEndian(handshakeSpan[16..], (ushort)(NbdHandshakeFlagFixedNewStyle | NbdHandshakeFlagNoZeroes));
        stream.Write(handshakeSpan);

        var clientFlagsBuffer = new byte[4];
        await NbdEndpoint.ReadExactlyAsync(stream, clientFlagsBuffer, cancellationToken);
        var clientFlags = BinaryPrimitives.ReadUInt32BigEndian(clientFlagsBuffer);

        var optionHeader = new byte[NbdOptionHeaderBytes];
        var state = new NbdConnectionState(ClientSupportsNoZeroes: (clientFlags & NbdHandshakeFlagNoZeroes) != 0);
        while (true)
        {
            await NbdEndpoint.ReadExactlyAsync(stream, optionHeader, cancellationToken);
            var optionHeaderSpan = optionHeader.AsSpan();
            if (BinaryPrimitives.ReadUInt64BigEndian(optionHeaderSpan) != NbdOptionMagic)
            {
                throw new InvalidOperationException("Invalid NBD option magic");
            }

            var option = BinaryPrimitives.ReadUInt32BigEndian(optionHeaderSpan[8..]);
            var optionLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(optionHeaderSpan[12..]));
            var optionPayload = new byte[optionLength];
            await NbdEndpoint.ReadExactlyAsync(stream, optionPayload, cancellationToken);
            if (option is NbdOptionStructuredReply or NbdOptionExtendedHeaders or NbdOptionSetMetaContext)
            {
                logger.LogInformation("NBD option received option={Option}", option);
            }

            if (option == NbdOptionExportName)
            {
                await WriteExportInfoAsync(stream, state.ClientSupportsNoZeroes, getExportSizeBytes(), cancellationToken);
                return state with { EnterTransmission = true };
            }

            if (option == NbdOptionGo)
            {
                await WriteInfoRepliesAsync(stream, option, getExportSizeBytes(), cancellationToken);
                await WriteOptionReplyAsync(stream, option, NbdReplyTypeAck, ReadOnlyMemory<byte>.Empty, cancellationToken);
                return state with { EnterTransmission = true };
            }

            if (option == NbdOptionInfo)
            {
                await WriteInfoRepliesAsync(stream, option, getExportSizeBytes(), cancellationToken);
                await WriteOptionReplyAsync(stream, option, NbdReplyTypeAck, ReadOnlyMemory<byte>.Empty, cancellationToken);
                continue;
            }

            if (option == NbdOptionAbort)
            {
                await WriteOptionReplyAsync(stream, option, NbdReplyTypeAck, ReadOnlyMemory<byte>.Empty, cancellationToken);
                return state;
            }

            if (option == NbdOptionList)
            {
                await WriteExportListReplyAsync(stream, option, cancellationToken);
                await WriteOptionReplyAsync(stream, option, NbdReplyTypeAck, ReadOnlyMemory<byte>.Empty, cancellationToken);
                continue;
            }

            if (option == NbdOptionStructuredReply)
            {
                state = state with { StructuredRepliesEnabled = true };
                await WriteOptionReplyAsync(stream, option, NbdReplyTypeAck, ReadOnlyMemory<byte>.Empty, cancellationToken);
                continue;
            }

            if (option == NbdOptionExtendedHeaders)
            {
                state = state with { ExtendedHeadersEnabled = true };
                await WriteOptionReplyAsync(stream, option, NbdReplyTypeAck, ReadOnlyMemory<byte>.Empty, cancellationToken);
                continue;
            }

            if (option is NbdOptionListMetaContext or NbdOptionSetMetaContext)
            {
                if (option == NbdOptionSetMetaContext && state.StructuredRepliesEnabled)
                {
                    state = state with { BlockStatusContextId = NbdProtocolCodec.GetBaseAllocationContextId(optionPayload) };
                }

                if (state.StructuredRepliesEnabled)
                {
                    await WriteMetaContextReplyAsync(stream, option, cancellationToken);
                }

                await WriteOptionReplyAsync(stream, option, NbdReplyTypeAck, ReadOnlyMemory<byte>.Empty, cancellationToken);
                continue;
            }

            await WriteOptionReplyAsync(stream, option, NbdReplyTypeErrUnsupported, ReadOnlyMemory<byte>.Empty, cancellationToken);
        }
    }

    private static async Task WriteExportInfoAsync(NetworkStream stream, bool clientSupportsNoZeroes, long exportSizeBytes, CancellationToken cancellationToken)
    {
        var span = (stackalloc byte[10]);
        BinaryPrimitives.WriteUInt64BigEndian(span, unchecked((ulong)exportSizeBytes));
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], (ushort)(NbdFlagHasFlags | NbdFlagSendFlush | NbdFlagSendTrim | NbdFlagSendWriteZeroes | NbdFlagSendCache | NbdFlagSendFastZero | NbdFlagSendBlockStatus | NbdFlagCanResize));
        await stream.WriteAsync(span.ToArray(), cancellationToken);
        if (!clientSupportsNoZeroes)
        {
            await stream.WriteAsync(new byte[124], cancellationToken);
        }
    }

    private static async Task WriteInfoRepliesAsync(NetworkStream stream, uint option, long exportSizeBytes, CancellationToken cancellationToken)
    {
        await WriteInfoReplyAsync(stream, option, NbdInfoExport, CreateExportInfoPayload(exportSizeBytes), cancellationToken);
        await WriteInfoReplyAsync(stream, option, NbdInfoName, Encoding.UTF8.GetBytes("teledisk"), cancellationToken);
        await WriteInfoReplyAsync(stream, option, NbdInfoDescription, Encoding.UTF8.GetBytes("telegram-backed export"), cancellationToken);
        await WriteInfoReplyAsync(stream, option, NbdInfoBlockSize, CreateBlockSizePayload(), cancellationToken);
    }

    private static byte[] CreateExportInfoPayload(long exportSizeBytes)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(payload, NbdInfoExport);
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(2), unchecked((ulong)exportSizeBytes));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(10), (ushort)(NbdFlagHasFlags | NbdFlagSendFlush | NbdFlagSendTrim | NbdFlagSendWriteZeroes | NbdFlagSendCache | NbdFlagSendFastZero | NbdFlagSendBlockStatus | NbdFlagCanResize));
        return payload;
    }

    private static byte[] CreateBlockSizePayload()
    {
        var payload = new byte[14];
        BinaryPrimitives.WriteUInt16BigEndian(payload, NbdInfoBlockSize);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(6), VirtualDiskLayout.ChunkSizeBytes);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(10), VirtualDiskLayout.MaxChunkPayloadBytes);
        return payload;
    }

    private static Task WriteExportListReplyAsync(NetworkStream stream, uint option, CancellationToken cancellationToken) =>
        WriteOptionReplyAsync(stream, option, NbdReplyTypeServer, Encoding.UTF8.GetBytes("teledisk"), cancellationToken);

    private static Task WriteMetaContextReplyAsync(NetworkStream stream, uint option, CancellationToken cancellationToken)
    {
        var name = Encoding.UTF8.GetBytes("base:allocation");
        var payload = new byte[4 + name.Length];
        BinaryPrimitives.WriteUInt32BigEndian(payload, NbdMetaContextBaseAllocation);
        name.CopyTo(payload.AsSpan(4));
        return WriteOptionReplyAsync(stream, option, NbdReplyTypeMetaContext, payload, cancellationToken);
    }

    private static async Task WriteInfoReplyAsync(NetworkStream stream, uint option, ushort infoType, byte[] payload, CancellationToken cancellationToken)
    {
        payload[0] = (byte)(infoType >> 8);
        payload[1] = (byte)infoType;
        await WriteOptionReplyAsync(stream, option, NbdReplyTypeInfo, payload, cancellationToken);
    }

    private static async Task WriteOptionReplyAsync(NetworkStream stream, uint option, uint replyType, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var span = (stackalloc byte[NbdOptionReplyHeaderBytes]);
        BinaryPrimitives.WriteUInt64BigEndian(span, NbdOptionReplyMagic);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], option);
        BinaryPrimitives.WriteUInt32BigEndian(span[12..], replyType);
        BinaryPrimitives.WriteUInt32BigEndian(span[16..], checked((uint)payload.Length));
        stream.Write(span);
        if (!payload.IsEmpty)
        {
            await stream.WriteAsync(payload, cancellationToken);
        }
    }
}
