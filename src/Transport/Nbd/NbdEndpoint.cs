using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TeleDisk.Application;
using TeleDisk.Domain.Storage;

namespace TeleDisk.Transport.Nbd;

internal sealed class NbdEndpoint(VirtualDiskService virtualDiskService, ILogger<NbdEndpoint> logger)
{
    private const int Port = 10809;
    private const ulong NbdMagic = 0x4e42444d41474943;
    private const ulong NbdOptionMagic = 0x49484156454F5054;
    private const uint NbdRequestMagic = 0x25609513;
    private const uint NbdReplyMagic = 0x67446698;
    private const uint NbdErrorInvalidArgument = 22;
    private const uint NbdErrorNotSupported = 95;
    private const uint NbdFlagHasFlags = 1;
    private const uint NbdFlagSendFlush = 1 << 2;
    private const uint NbdFlagSendWriteZeroes = 1 << 6;
    private const uint NbdFlagFixedNewStyle = 1;
    private const uint NbdOptionExportName = 1;
    private const uint NbdOptionAbort = 2;
    private const uint NbdOptionList = 3;
    private const uint NbdReplyTypeAck = 1;
    private const uint NbdReplyTypeServer = 2;
    private const uint NbdReplyTypeError = 0x80000001;
    private const int NbdRequestBytes = 28;
    private const int NbdReplyBytes = 16;
    private const int NbdOptionHeaderBytes = 16;
    private const int NbdOptionReplyHeaderBytes = 20;

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
                await HandleClientAsync(client, cancellationToken);
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
        await NegotiateNewStyleAsync(stream, cancellationToken);
        await ServeTransmissionAsync(stream, cancellationToken);
    }

    private static async Task NegotiateNewStyleAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
                Span<byte> handshakeSpan = stackalloc byte[18];
        BinaryPrimitives.WriteUInt64BigEndian(handshakeSpan, NbdMagic);
        BinaryPrimitives.WriteUInt64BigEndian(handshakeSpan[8..], NbdOptionMagic);
        BinaryPrimitives.WriteUInt16BigEndian(handshakeSpan[16..], (ushort)NbdFlagFixedNewStyle);
        stream.Write(handshakeSpan);

        Memory<byte> clientFlags = new byte[4];
        await ReadExactlyAsync(stream, clientFlags, cancellationToken);

        var optionHeader = new byte[NbdOptionHeaderBytes];
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
                await WriteExportInfoAsync(stream, cancellationToken);
                return;
            }

            if (option == NbdOptionAbort)
            {
                await WriteOptionReplyAsync(stream, option, NbdReplyTypeAck, ReadOnlyMemory<byte>.Empty, cancellationToken);
                throw new EndOfStreamException("Client aborted NBD negotiation");
            }

            if (option == NbdOptionList)
            {
                await WriteOptionReplyAsync(stream, option, NbdReplyTypeServer, ReadOnlyMemory<byte>.Empty, cancellationToken);
                await WriteOptionReplyAsync(stream, option, NbdReplyTypeAck, ReadOnlyMemory<byte>.Empty, cancellationToken);
                continue;
            }

            await WriteOptionReplyAsync(stream, option, NbdReplyTypeError, ReadOnlyMemory<byte>.Empty, cancellationToken);
        }
    }

    private static async Task WriteExportInfoAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        Span<byte> span = stackalloc byte[134];
        BinaryPrimitives.WriteUInt64BigEndian(span, unchecked((ulong)VirtualDiskLayout.CapacityBytes));
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], (ushort)(NbdFlagHasFlags | NbdFlagSendFlush | NbdFlagSendWriteZeroes));
        stream.Write(span);
    }

    private static async Task WriteOptionReplyAsync(NetworkStream stream, uint option, uint replyType, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        Span<byte> span = stackalloc byte[NbdOptionReplyHeaderBytes];
        BinaryPrimitives.WriteUInt64BigEndian(span, NbdOptionMagic);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], option);
        BinaryPrimitives.WriteUInt32BigEndian(span[12..], replyType);
        BinaryPrimitives.WriteUInt32BigEndian(span[16..], checked((uint)payload.Length));
        stream.Write(span);
        if (!payload.IsEmpty)
        {
            await stream.WriteAsync(payload, cancellationToken);
        }
    }

    private async Task ServeTransmissionAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var requestBuffer = new byte[NbdRequestBytes];
        while (true)
        {
            if (!await ReadExactlyOrFalseAsync(stream, requestBuffer, cancellationToken))
            {
                return;
            }

            var requestSpan = requestBuffer.AsSpan();
            var magic = BinaryPrimitives.ReadUInt32BigEndian(requestSpan);
            if (magic != NbdRequestMagic)
            {
                throw new InvalidOperationException($"Invalid NBD request magic: 0x{magic:X8}");
            }

            var command = (NbdCommand)(BinaryPrimitives.ReadUInt32BigEndian(requestSpan[4..]) & 0xFFFF);
            var handle = requestBuffer.AsMemory(8, 8);
            var offset = checked((long)BinaryPrimitives.ReadUInt64BigEndian(requestSpan[16..]));
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(requestSpan[24..]));

            if (length > VirtualDiskLayout.MaxChunkPayloadBytes)
            {
                await WriteReplyAsync(stream, handle, NbdErrorInvalidArgument, cancellationToken);
                return;
            }

            switch (command)
            {
                case NbdCommand.Read:
                {
                    var readData = new byte[length];
                    await virtualDiskService.ReadAsync(offset, readData, cancellationToken);
                    await WriteReplyAsync(stream, handle, 0, cancellationToken);
                    await stream.WriteAsync(readData, cancellationToken);
                    break;
                }
                case NbdCommand.Write:
                {
                    var writeData = new byte[length];
                    await ReadExactlyAsync(stream, writeData, cancellationToken);
                    await virtualDiskService.WriteAsync(offset, writeData, cancellationToken);
                    await WriteReplyAsync(stream, handle, 0, cancellationToken);
                    break;
                }
                case NbdCommand.Disconnect:
                    await virtualDiskService.SaveAsync(cancellationToken);
                    return;
                case NbdCommand.Flush:
                    await virtualDiskService.SaveAsync(cancellationToken);
                    await WriteReplyAsync(stream, handle, 0, cancellationToken);
                    break;
                default:
                    await WriteReplyAsync(stream, handle, NbdErrorNotSupported, cancellationToken);
                    break;
            }
        }
    }

    private static async Task WriteReplyAsync(NetworkStream stream, ReadOnlyMemory<byte> handle, uint error, CancellationToken cancellationToken)
    {
        Span<byte> span = stackalloc byte[NbdReplyBytes];
        BinaryPrimitives.WriteUInt32BigEndian(span, NbdReplyMagic);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], error);
        handle.Span.CopyTo(span[8..]);
        stream.Write(span);
    }

    private static async Task<bool> ReadExactlyOrFalseAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken) =>
        await stream.ReadAtLeastAsync(buffer, buffer.Length, false, cancellationToken) == buffer.Length;

    private static async Task ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (!await ReadExactlyOrFalseAsync(stream, buffer, cancellationToken))
        {
            throw new EndOfStreamException();
        }
    }
}

internal enum NbdCommand
{
    Read = 0,
    Write = 1,
    Disconnect = 2,
    Flush = 3
}
