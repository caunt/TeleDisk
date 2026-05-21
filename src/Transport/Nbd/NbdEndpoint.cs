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
    private const ulong NbdOldStyleMagic = 0x0000420281861253;
    private const uint NbdRequestMagic = 0x25609513;
    private const uint NbdReplyMagic = 0x67446698;
    private const uint NbdErrorInvalidArgument = 22;
    private const uint NbdErrorNotSupported = 95;
    private const int NbdRequestBytes = 28;
    private const int NbdHandshakeBytes = 152;
    private const int NbdReplyBytes = 16;

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Any, Port);
        listener.Start();
        logger.LogInformation("NBD listening on 0.0.0.0:{Port}", Port);
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;
                logger.LogInformation("NBD client connected");
                try
                {
                    await HandleClientAsync(client, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "NBD client failed");
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
        await WriteHandshakeAsync(stream, cancellationToken);
        var requestBuffer = new byte[NbdRequestBytes];
        while (true)
        {
            if (!await ReadExactlyOrFalseAsync(stream, requestBuffer, cancellationToken))
            {
                return;
            }

            var magic = BinaryPrimitives.ReadUInt32BigEndian(requestBuffer);
            if (magic != NbdRequestMagic)
            {
                throw new InvalidOperationException($"Invalid NBD request magic: 0x{magic:X8}");
            }

            var command = (NbdCommand)(BinaryPrimitives.ReadUInt32BigEndian(requestBuffer.AsSpan(4)) & 0xFFFF);
            var handle = requestBuffer.AsMemory(8, 8);
            var offset = checked((long)BinaryPrimitives.ReadUInt64BigEndian(requestBuffer.AsSpan(16)));
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(requestBuffer.AsSpan(24)));

            if (length > VirtualDiskLayout.MaxChunkPayloadBytes)
            {
                await WriteReplyAsync(stream, handle, NbdErrorInvalidArgument, cancellationToken);
                return;
            }

            switch (command)
            {
                case NbdCommand.Read:
                    var readData = new byte[length];
                    await virtualDiskService.ReadAsync(offset, readData, cancellationToken);
                    await WriteReplyAsync(stream, handle, 0, cancellationToken);
                    await stream.WriteAsync(readData, cancellationToken);
                    break;
                case NbdCommand.Write:
                    var writeData = new byte[length];
                    await ReadExactlyAsync(stream, writeData, cancellationToken);
                    await virtualDiskService.WriteAsync(offset, writeData, cancellationToken);
                    await WriteReplyAsync(stream, handle, 0, cancellationToken);
                    break;
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

    private static async Task WriteHandshakeAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var handshakeBuffer = new byte[NbdHandshakeBytes];
        BinaryPrimitives.WriteUInt64BigEndian(handshakeBuffer, NbdMagic);
        BinaryPrimitives.WriteUInt64BigEndian(handshakeBuffer.AsSpan(8), NbdOldStyleMagic);
        BinaryPrimitives.WriteUInt64BigEndian(handshakeBuffer.AsSpan(16), unchecked((ulong)VirtualDiskLayout.CapacityBytes));
        await stream.WriteAsync(handshakeBuffer, cancellationToken);
    }

    private static async Task WriteReplyAsync(NetworkStream stream, ReadOnlyMemory<byte> handle, uint error, CancellationToken cancellationToken)
    {
        var response = new byte[NbdReplyBytes];
        BinaryPrimitives.WriteUInt32BigEndian(response, NbdReplyMagic);
        BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(4), error);
        handle.CopyTo(response.AsMemory(8));
        await stream.WriteAsync(response, cancellationToken);
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
