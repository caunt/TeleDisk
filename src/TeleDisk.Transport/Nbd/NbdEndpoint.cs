using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TeleDisk.Application;
using TeleDisk.Domain.Storage;
using TeleDisk.Infrastructure.Telegram;

namespace TeleDisk.Transport.Nbd;

internal sealed class NbdEndpoint(VirtualDiskService virtualDiskService, ILogger<NbdEndpoint> logger)
{
    private long _exportSizeBytes = VirtualDiskLayout.CapacityBytes;
    private const int Port = 10809;
    private const uint NbdErrorInvalidArgument = 22;
    private const uint NbdErrorNotSupported = 95;
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
    private const uint NbdFlagHasFlags = 1;
    private const uint NbdFlagSendFlush = 1 << 2;
    private const uint NbdFlagSendTrim = 1 << 5;
    private const uint NbdFlagSendWriteZeroes = 1 << 6;
    private const uint NbdFlagSendCache = 1 << 10;
    private const uint NbdFlagSendFastZero = 1 << 11;
    private const uint NbdFlagSendBlockStatus = 1 << 12;
    private const uint NbdFlagCanResize = 1 << 13;
    private const ushort NbdStructuredReplyTypeBlockStatus = 5;
    private const ushort NbdStructuredReplyTypeOffsetData = 1;
    private const ushort NbdStructuredReplyTypeErrorUnknown = 0x8001;
    private const ushort NbdStructuredReplyFlagDone = 1;
    private const uint NbdStateHole = 1;
    private const uint NbdStateZero = 1 << 1;
    private const int NbdRequestBytes = 28;
    private const int NbdExtendedRequestBytes = 32;

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

    private Task<NbdConnectionState> NegotiateNewStyleAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        return new NbdHandshakeNegotiator(logger, GetExportSizeBytes).NegotiateAsync(stream, cancellationToken);
    }

    private async Task ServeTransmissionAsync(NetworkStream stream, NbdConnectionState state, CancellationToken cancellationToken)
    {
        state = state with
        {
            BlockStatusContextId = state.StructuredRepliesEnabled ? state.BlockStatusContextId : null
        };
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

                if (!NbdProtocolCodec.TryParseRequestHeader(requestBuffer.AsSpan(0, headerLength), state.ExtendedHeadersEnabled, out var requestHeader))
                {
                    throw new InvalidOperationException($"Invalid NBD request magic: 0x{BinaryPrimitives.ReadUInt32BigEndian(requestBuffer):X8}");
                }

                var validatedRequest = await TryValidateRequestAsync(stream, requestHeader, state, cancellationToken);
                if (validatedRequest is null)
                {
                    continue;
                }
                var (offset, length) = validatedRequest.Value;

                if (requestHeader.Command == NbdCommand.Disconnect)
                {
                    await SaveBestEffortAsync(cancellationToken);
                    return;
                }

                await ExecuteRequestAsync(stream, requestHeader, offset, length, state, cancellationToken);
            }
        }
        catch (IOException exception) when (exception.InnerException is SocketException socketException &&
                                            (socketException.NativeErrorCode == 32 || socketException.SocketErrorCode == SocketError.ConnectionReset))
        {
            logger.LogDebug(exception, "NBD client disconnected while awaiting reply");
        }
    }


    private bool TryValidateRequestHeaderRange(NbdRequestHeader requestHeader, out long offset, out int length)
    {
        offset = default;
        length = default;
        if (requestHeader.Offset > long.MaxValue || requestHeader.Length > int.MaxValue)
        {
            return false;
        }

        offset = (long)requestHeader.Offset;
        length = (int)requestHeader.Length;
        return length <= VirtualDiskLayout.MaxChunkPayloadBytes;
    }

    private async Task<(long Offset, int Length)?> TryValidateRequestAsync(NetworkStream stream, NbdRequestHeader requestHeader, NbdConnectionState state, CancellationToken cancellationToken)
    {
        if (!TryValidateRequestHeaderRange(requestHeader, out var offset, out var length))
        {
            await WriteReplyAsync(stream, requestHeader.Handle, NbdErrorInvalidArgument, state.StructuredRepliesEnabled, state.ExtendedHeadersEnabled, cancellationToken);
            return null;
        }

        if (IsRangeWithinExport(offset, length, requestHeader.Command))
        {
            return (offset, length);
        }

        LogReadFailure(requestHeader.Command, requestHeader.Handle.Span, offset, length, GetExportSizeBytes(), state.StructuredRepliesEnabled, NbdErrorInvalidArgument);
        await WriteReplyAsync(stream, requestHeader.Handle, NbdErrorInvalidArgument, state.StructuredRepliesEnabled, state.ExtendedHeadersEnabled, cancellationToken);
        return null;
    }

    private async Task ExecuteRequestAsync(NetworkStream stream, NbdRequestHeader requestHeader, long offset, int length, NbdConnectionState state, CancellationToken cancellationToken)
    {
        try
        {
            switch (requestHeader.Command)
            {
                case NbdCommand.Read:
                    await HandleReadAsync(stream, requestHeader.Handle, offset, length, requestHeader.Flags, state, cancellationToken);
                    return;
                case NbdCommand.Write:
                    await HandleWriteAsync(stream, requestHeader.Handle, offset, length, state.ExtendedHeadersEnabled, cancellationToken);
                    return;
                case NbdCommand.Flush:
                    await SaveBestEffortAsync(cancellationToken);
                    await WriteReplyAsync(stream, requestHeader.Handle, 0, false, state.ExtendedHeadersEnabled, cancellationToken);
                    return;
                case NbdCommand.Trim:
                case NbdCommand.Cache:
                    await WriteReplyAsync(stream, requestHeader.Handle, 0, false, state.ExtendedHeadersEnabled, cancellationToken);
                    return;
                case NbdCommand.Resize:
                    await HandleResizeAsync(stream, requestHeader, offset, state, cancellationToken);
                    return;
                case NbdCommand.WriteZeroes:
                    await virtualDiskService.WriteZeroesAsync(offset, length, cancellationToken);
                    await WriteReplyAsync(stream, requestHeader.Handle, 0, false, state.ExtendedHeadersEnabled, cancellationToken);
                    return;
                case NbdCommand.BlockStatus:
                    await HandleBlockStatusAsync(stream, requestHeader.Handle, offset, length, state, cancellationToken);
                    return;
                default:
                    await WriteReplyAsync(stream, requestHeader.Handle, NbdErrorNotSupported, state.StructuredRepliesEnabled, state.ExtendedHeadersEnabled, cancellationToken);
                    return;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            LogReadFailure(requestHeader.Command, requestHeader.Handle.Span, offset, length, GetExportSizeBytes(), state.StructuredRepliesEnabled, NbdErrorInvalidArgument);
            await WriteReplyAsync(stream, requestHeader.Handle, NbdErrorInvalidArgument, state.StructuredRepliesEnabled, state.ExtendedHeadersEnabled, cancellationToken);
        }
    }

    private async Task HandleResizeAsync(NetworkStream stream, NbdRequestHeader requestHeader, long offset, NbdConnectionState state, CancellationToken cancellationToken)
    {
        if (offset <= 0 || offset > VirtualDiskLayout.CapacityBytes)
        {
            await WriteReplyAsync(stream, requestHeader.Handle, NbdErrorInvalidArgument, state.StructuredRepliesEnabled, state.ExtendedHeadersEnabled, cancellationToken);
            return;
        }

        SetExportSizeBytes(offset);
        await WriteReplyAsync(stream, requestHeader.Handle, 0, false, state.ExtendedHeadersEnabled, cancellationToken);
    }

    private async Task HandleReadAsync(NetworkStream stream, ReadOnlyMemory<byte> handle, long offset, int length, ushort commandFlags, NbdConnectionState state, CancellationToken cancellationToken)
    {
        var readData = new byte[length];
        await virtualDiskService.ReadAsync(offset, readData, cancellationToken);
        _ = commandFlags;
        var useStructuredReply = state.StructuredRepliesEnabled || state.ExtendedHeadersEnabled;
        if (useStructuredReply)
        {
            await WriteStructuredReadReplyAsync(stream, handle, offset, readData, state.ExtendedHeadersEnabled, cancellationToken);
            return;
        }

        await WriteReplyAsync(stream, handle, 0, false, state.ExtendedHeadersEnabled, cancellationToken);
        await stream.WriteAsync(readData, cancellationToken);
    }

    private async Task HandleWriteAsync(NetworkStream stream, ReadOnlyMemory<byte> handle, long offset, int length, bool useExtendedHeaders, CancellationToken cancellationToken)
    {
        var writeData = new byte[length];
        await ReadExactlyAsync(stream, writeData, cancellationToken);
        await virtualDiskService.WriteAsync(offset, writeData, cancellationToken);
        await WriteReplyAsync(stream, handle, 0, false, useExtendedHeaders, cancellationToken);
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

        await WriteReplyAsync(stream, handle, NbdErrorNotSupported, false, false, cancellationToken);
    }

    private async Task HandleBlockStatusAsync(NetworkStream stream, ReadOnlyMemory<byte> handle, long offset, int length, NbdConnectionState state, CancellationToken cancellationToken)
    {
        if (!state.StructuredRepliesEnabled || state.BlockStatusContextId is null)
        {
            await WriteReplyAsync(stream, handle, NbdErrorNotSupported, true, state.ExtendedHeadersEnabled, cancellationToken);
            return;
        }

        var isAllocated = await virtualDiskService.IsAllocatedAsync(offset, cancellationToken);
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(payload, state.BlockStatusContextId.Value);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), checked((uint)length));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8), isAllocated ? 0U : NbdStateHole | NbdStateZero);
        await WriteStructuredReplyAsync(stream, handle, NbdStructuredReplyTypeBlockStatus, NbdStructuredReplyFlagDone, payload, state.ExtendedHeadersEnabled, cancellationToken, checked((ulong)offset));
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

    internal static byte[] BuildStructuredReplyBytes(ReadOnlySpan<byte> handle, ushort replyType, ushort flags, ReadOnlySpan<byte> payload, bool useExtendedHeaders = false, ulong offset = 0)
        => NbdProtocolCodec.BuildStructuredReplyBytes(handle, replyType, flags, payload, useExtendedHeaders, offset);

    private static async Task WriteStructuredReplyAsync(NetworkStream stream, ReadOnlyMemory<byte> handle, ushort replyType, ushort flags, ReadOnlyMemory<byte> payload, bool useExtendedHeaders, CancellationToken cancellationToken, ulong offset = 0)
    {
        var bytes = NbdProtocolCodec.BuildStructuredReplyBytes(handle.Span, replyType, flags, payload.Span, useExtendedHeaders, offset);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static async Task WriteStructuredReadReplyAsync(NetworkStream stream, ReadOnlyMemory<byte> handle, long offset, ReadOnlyMemory<byte> payload, bool useExtendedHeaders, CancellationToken cancellationToken)
    {
        await WriteStructuredReplyAsync(stream, handle, NbdStructuredReplyTypeOffsetData, NbdStructuredReplyFlagDone, NbdProtocolCodec.BuildStructuredReadPayload(offset, payload.Span), useExtendedHeaders, cancellationToken, checked((ulong)offset));
    }

    internal static byte[] BuildStructuredReadPayload(long offset, ReadOnlySpan<byte> payload)
        => NbdProtocolCodec.BuildStructuredReadPayload(offset, payload);

    internal static byte[] BuildStructuredErrorPayload(uint error)
        => NbdProtocolCodec.BuildStructuredErrorPayload(error);

    private static async Task WriteStructuredErrorReplyAsync(NetworkStream stream, ReadOnlyMemory<byte> handle, uint error, bool useExtendedHeaders, CancellationToken cancellationToken)
    {
        await WriteStructuredReplyAsync(stream, handle, NbdStructuredReplyTypeErrorUnknown, NbdStructuredReplyFlagDone, NbdProtocolCodec.BuildStructuredErrorPayload(error), useExtendedHeaders, cancellationToken);
    }

    private static async Task WriteReplyAsync(NetworkStream stream, ReadOnlyMemory<byte> handle, uint error, bool useStructuredErrorReply, bool useExtendedHeaders, CancellationToken cancellationToken, ulong offset = 0)
    {
        if (useExtendedHeaders && error == 0)
        {
            stream.Write(NbdProtocolCodec.BuildSimpleReplyBytes(handle.Span, 0));
            return;
        }

        if (useExtendedHeaders || (error != 0 && useStructuredErrorReply))
        {
            var replyType = error == 0 ? (ushort)0 : NbdStructuredReplyTypeErrorUnknown;
            var payload = error == 0 ? ReadOnlyMemory<byte>.Empty : NbdProtocolCodec.BuildStructuredErrorPayload(error);
            await WriteStructuredReplyAsync(stream, handle, replyType, NbdStructuredReplyFlagDone, payload, useExtendedHeaders, cancellationToken, offset);
            return;
        }

        var bytes = NbdProtocolCodec.BuildSimpleReplyBytes(handle.Span, error);
        stream.Write(bytes);
        await Task.CompletedTask;
    }

    internal static byte[] BuildSimpleReplyBytes(ReadOnlySpan<byte> handle, uint error)
        => NbdProtocolCodec.BuildSimpleReplyBytes(handle, error);

    internal static bool TryParseRequestHeader(ReadOnlySpan<byte> requestBytes, bool useExtendedHeaders, out NbdRequestHeader requestHeader)
        => NbdProtocolCodec.TryParseRequestHeader(requestBytes, useExtendedHeaders, out requestHeader);


    private long GetExportSizeBytes() => Interlocked.Read(ref _exportSizeBytes);

    private void SetExportSizeBytes(long exportSizeBytes) => Interlocked.Exchange(ref _exportSizeBytes, exportSizeBytes);

    private static async Task<bool> ReadExactlyOrFalseAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken) =>
        await stream.ReadAtLeastAsync(buffer, buffer.Length, false, cancellationToken) == buffer.Length;

    internal static async Task ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (!await ReadExactlyOrFalseAsync(stream, buffer, cancellationToken))
        {
            throw new EndOfStreamException();
        }
    }

    internal static uint? GetBaseAllocationContextId(ReadOnlySpan<byte> payload)
        => NbdProtocolCodec.GetBaseAllocationContextId(payload);
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

internal readonly record struct NbdRequestHeader(ushort Flags, NbdCommand Command, ReadOnlyMemory<byte> Handle, ulong Offset, ulong Length);

internal sealed record NbdConnectionState(bool EnterTransmission = false, bool StructuredRepliesEnabled = false, bool ClientSupportsNoZeroes = false, uint? BlockStatusContextId = null, bool ExtendedHeadersEnabled = false);
