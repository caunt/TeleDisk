using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using TeleDisk.Application;
using TeleDisk.Domain.Storage;
using TeleDisk.Infrastructure.Telegram;

namespace TeleDisk.Transport.Nbd;

internal sealed class NbdEndpoint(VirtualDiskService virtualDiskService, ILogger<NbdEndpoint> logger)
{
    private long _exportSizeBytes = VirtualDiskLayout.CapacityBytes;
    private const int Port = 10809;
    private const ulong NbdMagic = 0x4e42444d41474943;
    private const ulong NbdOptionMagic = 0x49484156454F5054;
    private const ulong NbdOptionReplyMagic = 0x3e889045565a9;
    private const uint NbdRequestMagic = 0x25609513;
    private const uint NbdReplyMagic = 0x67446698;
    private const uint NbdStructuredReplyMagic = 0x668e33ef;
    private const uint NbdErrorInvalidArgument = 22;
    private const uint NbdErrorNotSupported = 95;
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
    private const uint NbdOptionPeekExport = 4;
    private const uint NbdOptionStartTls = 5;
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
    private const ushort NbdStructuredReplyTypeBlockStatus = 5;
    private const ushort NbdStructuredReplyTypeOffsetData = 1;
    private const ushort NbdStructuredReplyTypeErrorUnknown = 0x8001;
    private const ushort NbdStructuredReplyFlagDone = 1;
    private const uint NbdStateHole = 1;
    private const uint NbdStateZero = 1 << 1;
    private const int NbdRequestBytes = 28;
    private const int NbdExtendedRequestBytes = 32;
    private const int NbdReplyBytes = 16;
    private const int NbdOptionHeaderBytes = 16;
    private const int NbdOptionReplyHeaderBytes = 20;
    private const int NbdStructuredReplyHeaderBytes = 20;

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Any, Port);
        listener.Start();
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;

                try
                {
                    await HandleClientAsync(client, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "NBD client session ended with an error");
                }
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        var state = await NegotiateNewStyleAsync(stream, cancellationToken);
        if (state.EnterTransmission)
        {
            await ServeTransmissionAsync(stream, state, cancellationToken);
        }
    }

    private async Task<NbdConnectionState> NegotiateNewStyleAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        _ = logger;
        var handshakeSpan = (stackalloc byte[18]);
        BinaryPrimitives.WriteUInt64BigEndian(handshakeSpan, NbdMagic);
        BinaryPrimitives.WriteUInt64BigEndian(handshakeSpan[8..], NbdOptionMagic);
        BinaryPrimitives.WriteUInt16BigEndian(handshakeSpan[16..], (ushort)(NbdHandshakeFlagFixedNewStyle | NbdHandshakeFlagNoZeroes));
        stream.Write(handshakeSpan);

        var clientFlagsBuffer = new byte[4];
        await ReadExactlyAsync(stream, clientFlagsBuffer, cancellationToken);
        var clientFlags = BinaryPrimitives.ReadUInt32BigEndian(clientFlagsBuffer);

        var optionHeader = new byte[NbdOptionHeaderBytes];
        var state = new NbdConnectionState(ClientSupportsNoZeroes: (clientFlags & NbdHandshakeFlagNoZeroes) != 0);
        while (true)
        {
            await ReadExactlyAsync(stream, optionHeader, cancellationToken);
            var optionHeaderSpan = optionHeader.AsSpan();
            if (BinaryPrimitives.ReadUInt64BigEndian(optionHeaderSpan) != NbdOptionMagic)
            {
                throw new InvalidOperationException("Invalid NBD option magic");
            }

            var option = BinaryPrimitives.ReadUInt32BigEndian(optionHeaderSpan[8..]);
            var optionLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(optionHeaderSpan[12..]));
            var optionPayload = new byte[optionLength];
            await ReadExactlyAsync(stream, optionPayload, cancellationToken);

            if (option == NbdOptionExportName)
            {
                await WriteExportInfoAsync(stream, state.ClientSupportsNoZeroes, GetExportSizeBytes(), cancellationToken);
                return state with { EnterTransmission = true };
            }

            if (option == NbdOptionGo)
            {
                await WriteInfoRepliesAsync(stream, option, GetExportSizeBytes(), cancellationToken);
                await WriteOptionReplyAsync(stream, option, NbdReplyTypeAck, ReadOnlyMemory<byte>.Empty, cancellationToken);
                return state with { EnterTransmission = true };
            }

            if (option == NbdOptionInfo)
            {
                await WriteInfoRepliesAsync(stream, option, GetExportSizeBytes(), cancellationToken);
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
                if (option == NbdOptionSetMetaContext)
                {
                    state = state with { BlockStatusContextId = GetBaseAllocationContextId(optionPayload) };
                }

                await WriteMetaContextReplyAsync(stream, option, cancellationToken);
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

    private async Task ServeTransmissionAsync(NetworkStream stream, NbdConnectionState state, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "NBD negotiation structuredRepliesEnabled={StructuredRepliesEnabled} blockStatusEnabled={BlockStatusEnabled} extendedHeadersEnabled={ExtendedHeadersEnabled}",
            state.StructuredRepliesEnabled,
            state.BlockStatusContextId is not null,
            state.ExtendedHeadersEnabled);
        var requestBuffer = new byte[NbdExtendedRequestBytes];
        try
        {
            while (true)
            {
                var headerLength = state.ExtendedHeadersEnabled ? NbdExtendedRequestBytes : NbdRequestBytes;
                if (!await ReadExactlyOrFalseAsync(stream, requestBuffer.AsMemory(0, headerLength), cancellationToken))
                {
                    return;
                }

                var requestSpan = requestBuffer.AsSpan();
                var magic = BinaryPrimitives.ReadUInt32BigEndian(requestSpan);
                if (magic != NbdRequestMagic)
                {
                    throw new InvalidOperationException($"Invalid NBD request magic: 0x{magic:X8}");
                }

                var commandFlags = BinaryPrimitives.ReadUInt16BigEndian(requestSpan[4..]);
                var command = (NbdCommand)(BinaryPrimitives.ReadUInt16BigEndian(requestSpan[6..]));
                var handle = requestBuffer.AsMemory(8, 8);
                var requestOffset = BinaryPrimitives.ReadUInt64BigEndian(requestSpan[16..]);
                var requestLength = BinaryPrimitives.ReadUInt32BigEndian(requestSpan[24..]);
                if (requestOffset > long.MaxValue || requestLength > int.MaxValue)
                {
                    await WriteReplyAsync(stream, handle, NbdErrorInvalidArgument, state.StructuredRepliesEnabled, cancellationToken);
                    continue;
                }

                var offset = (long)requestOffset;
                var length = (int)requestLength;

                if (length > VirtualDiskLayout.MaxChunkPayloadBytes)
                {
                    await WriteReplyAsync(stream, handle, NbdErrorInvalidArgument, state.StructuredRepliesEnabled, cancellationToken);
                    continue;
                }

                if (!IsRangeWithinExport(offset, length, command))
                {
                    LogReadFailure(command, handle.Span, offset, length, GetExportSizeBytes(), state.StructuredRepliesEnabled, NbdErrorInvalidArgument);
                    await WriteReplyAsync(stream, handle, NbdErrorInvalidArgument, state.StructuredRepliesEnabled, cancellationToken);
                    continue;
                }

                try
                {
                    switch (command)
                    {
                        case NbdCommand.Read:
                            await HandleReadAsync(stream, handle, offset, length, commandFlags, state, cancellationToken);
                            break;
                        case NbdCommand.Write:
                            await HandleWriteAsync(stream, handle, offset, length, cancellationToken);
                            break;
                        case NbdCommand.Disconnect:
                            await SaveBestEffortAsync(cancellationToken);
                            return;
                        case NbdCommand.Flush:
                            await SaveBestEffortAsync(cancellationToken);
                            await WriteReplyAsync(stream, handle, 0, false, cancellationToken);
                            break;
                        case NbdCommand.Trim:
                        case NbdCommand.Cache:
                            await WriteReplyAsync(stream, handle, 0, false, cancellationToken);
                            break;
                        case NbdCommand.Resize:
                            if (offset <= 0 || offset > VirtualDiskLayout.CapacityBytes)
                            {
                                await WriteReplyAsync(stream, handle, NbdErrorInvalidArgument, state.StructuredRepliesEnabled, cancellationToken);
                                break;
                            }

                            SetExportSizeBytes(offset);
                            await WriteReplyAsync(stream, handle, 0, false, cancellationToken);
                            break;
                        case NbdCommand.WriteZeroes:
                            await virtualDiskService.WriteZeroesAsync(offset, length, cancellationToken);
                            await WriteReplyAsync(stream, handle, 0, false, cancellationToken);
                            break;
                        case NbdCommand.BlockStatus:
                            await HandleBlockStatusAsync(stream, handle, offset, length, state, cancellationToken);
                            break;
                        default:
                            await WriteReplyAsync(stream, handle, NbdErrorNotSupported, state.StructuredRepliesEnabled, cancellationToken);
                            break;
                    }
                }
                catch (ArgumentOutOfRangeException)
                {
                    LogReadFailure(command, handle.Span, offset, length, GetExportSizeBytes(), state.StructuredRepliesEnabled, NbdErrorInvalidArgument);
                    await WriteReplyAsync(stream, handle, NbdErrorInvalidArgument, state.StructuredRepliesEnabled, cancellationToken);
                }
            }
        }
        catch (IOException exception) when (exception.InnerException is SocketException socketException &&
                                            (socketException.NativeErrorCode == 32 || socketException.SocketErrorCode == SocketError.ConnectionReset))
        {
            logger.LogDebug(exception, "NBD client disconnected while awaiting reply");
        }
    }

    private async Task HandleReadAsync(NetworkStream stream, ReadOnlyMemory<byte> handle, long offset, int length, ushort commandFlags, NbdConnectionState state, CancellationToken cancellationToken)
    {
        var readData = new byte[length];
        await virtualDiskService.ReadAsync(offset, readData, cancellationToken);
        _ = commandFlags;
        var useStructuredReply = state.StructuredRepliesEnabled;
        if (useStructuredReply)
        {
            await WriteStructuredReadReplyAsync(stream, handle, offset, readData, cancellationToken);
            return;
        }

        await WriteReplyAsync(stream, handle, 0, false, cancellationToken);
        await stream.WriteAsync(readData, cancellationToken);
    }

    private async Task HandleWriteAsync(NetworkStream stream, ReadOnlyMemory<byte> handle, long offset, int length, CancellationToken cancellationToken)
    {
        var writeData = new byte[length];
        await ReadExactlyAsync(stream, writeData, cancellationToken);
        await virtualDiskService.WriteAsync(offset, writeData, cancellationToken);
        await WriteReplyAsync(stream, handle, 0, false, cancellationToken);
    }

    private static async Task DrainAndReplyNotSupportedAsync(NetworkStream stream, ReadOnlyMemory<byte> handle, int payloadLength, CancellationToken cancellationToken)
    {
        var drainBuffer = new byte[Math.Max(1, Math.Min(payloadLength, 64 * 1024))];
        for (var drainedBytes = 0; drainedBytes < payloadLength;)
        {
            var toRead = Math.Min(drainBuffer.Length, payloadLength - drainedBytes);
            await ReadExactlyAsync(stream, drainBuffer.AsMemory(0, toRead), cancellationToken);
            drainedBytes += toRead;
        }

        await WriteReplyAsync(stream, handle, NbdErrorNotSupported, false, cancellationToken);
    }

    private async Task HandleBlockStatusAsync(NetworkStream stream, ReadOnlyMemory<byte> handle, long offset, int length, NbdConnectionState state, CancellationToken cancellationToken)
    {
        if (!state.StructuredRepliesEnabled || state.BlockStatusContextId is null)
        {
            await WriteReplyAsync(stream, handle, NbdErrorNotSupported, true, cancellationToken);
            return;
        }

        var isAllocated = await virtualDiskService.IsAllocatedAsync(offset, cancellationToken);
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(payload, state.BlockStatusContextId.Value);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), checked((uint)length));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8), isAllocated ? 0U : NbdStateHole | NbdStateZero);
        await WriteStructuredReplyAsync(stream, handle, NbdStructuredReplyTypeBlockStatus, NbdStructuredReplyFlagDone, payload, cancellationToken);
    }

    private bool IsRangeWithinExport(long offset, int length, NbdCommand command)
    {
        if (command is NbdCommand.Disconnect or NbdCommand.Flush or NbdCommand.Cache)
        {
            return true;
        }

        var exportSizeBytes = GetExportSizeBytes();
        if (offset < 0 || length < 0 || exportSizeBytes < 0)
        {
            return false;
        }

        return (ulong)offset + (uint)length <= (ulong)exportSizeBytes;
    }

    private void LogReadFailure(NbdCommand command, ReadOnlySpan<byte> handle, long offset, int length, long exportSizeBytes, bool negotiatedStructuredReplies, uint errorCode)
    {
        if (command is not NbdCommand.Read)
        {
            return;
        }

        logger.LogWarning(
            "NBD read failed command={Command} handle={Handle} offset={Offset} length={Length} exportSize={ExportSize} negotiatedStructuredReplies={StructuredReplies} errorCode={ErrorCode}",
            command,
            Convert.ToHexString(handle),
            offset,
            length,
            exportSizeBytes,
            negotiatedStructuredReplies,
            errorCode);
    }

    private async Task SaveBestEffortAsync(CancellationToken cancellationToken)
    {
        try
        {
            await virtualDiskService.SaveAsync(cancellationToken);
        }
        catch (TelegramRateLimitException exception)
        {
            logger.LogDebug(exception, "Deferred save due to Telegram retry_after={RetryAfterSeconds}s", exception.RetryAfter.TotalSeconds);
        }
    }

    internal static byte[] BuildStructuredReplyBytes(ReadOnlySpan<byte> handle, ushort replyType, ushort flags, ReadOnlySpan<byte> payload)
    {
        var bytes = new byte[NbdStructuredReplyHeaderBytes + payload.Length];
        var header = bytes.AsSpan(0, NbdStructuredReplyHeaderBytes);
        BinaryPrimitives.WriteUInt32BigEndian(header, NbdStructuredReplyMagic);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], flags);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], replyType);
        handle.CopyTo(header[8..]);
        BinaryPrimitives.WriteUInt32BigEndian(header[16..], checked((uint)payload.Length));
        payload.CopyTo(bytes.AsSpan(NbdStructuredReplyHeaderBytes));
        return bytes;
    }

    private static async Task WriteStructuredReplyAsync(NetworkStream stream, ReadOnlyMemory<byte> handle, ushort replyType, ushort flags, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var bytes = BuildStructuredReplyBytes(handle.Span, replyType, flags, payload.Span);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static async Task WriteStructuredReadReplyAsync(NetworkStream stream, ReadOnlyMemory<byte> handle, long offset, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await WriteStructuredReplyAsync(stream, handle, NbdStructuredReplyTypeOffsetData, NbdStructuredReplyFlagDone, BuildStructuredReadPayload(offset, payload.Span), cancellationToken);
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

    private static async Task WriteStructuredErrorReplyAsync(NetworkStream stream, ReadOnlyMemory<byte> handle, uint error, CancellationToken cancellationToken)
    {
        await WriteStructuredReplyAsync(stream, handle, NbdStructuredReplyTypeErrorUnknown, NbdStructuredReplyFlagDone, BuildStructuredErrorPayload(error), cancellationToken);
    }

    private static async Task WriteReplyAsync(NetworkStream stream, ReadOnlyMemory<byte> handle, uint error, bool useStructuredErrorReply, CancellationToken cancellationToken)
    {
        if (error != 0 && useStructuredErrorReply)
        {
            await WriteStructuredErrorReplyAsync(stream, handle, error, cancellationToken);
            return;
        }

        var span = (stackalloc byte[NbdReplyBytes]);
        BinaryPrimitives.WriteUInt32BigEndian(span, NbdReplyMagic);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], error);
        handle.Span.CopyTo(span[8..]);
        stream.Write(span);
        await Task.CompletedTask;
    }


    private long GetExportSizeBytes() => Interlocked.Read(ref _exportSizeBytes);

    private void SetExportSizeBytes(long exportSizeBytes) => Interlocked.Exchange(ref _exportSizeBytes, exportSizeBytes);

    private static async Task<bool> ReadExactlyOrFalseAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken) =>
        await stream.ReadAtLeastAsync(buffer, buffer.Length, false, cancellationToken) == buffer.Length;

    private static async Task ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (!await ReadExactlyOrFalseAsync(stream, buffer, cancellationToken))
        {
            throw new EndOfStreamException();
        }
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

internal enum NbdCommand : ushort
{
    Read = 0,
    Write = 1,
    Disconnect = 2,
    Flush = 3,
    Trim = 4,
    Cache = 5,
    WriteZeroes = 6,
    BlockStatus = 7,
    Resize = 8
}

internal sealed record NbdConnectionState(bool EnterTransmission = false, bool StructuredRepliesEnabled = false, bool ClientSupportsNoZeroes = false, uint? BlockStatusContextId = null, bool ExtendedHeadersEnabled = false);
