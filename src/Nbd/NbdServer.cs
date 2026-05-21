using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TeleDisk.Disk;

namespace TeleDisk.Nbd;

internal sealed class NbdServer(DiskService diskService, ILogger<NbdServer> logger) {
    const int Port = 10809;
    const ulong NbdMagic = 0x4e42444d41474943;
    const ulong NbdOldStyleMagic = 0x0000420281861253;
    const uint NbdRequestMagic = 0x25609513;
    const uint NbdReplyMagic = 0x67446698;
    const uint NbdErrorInvalidArgument = 22;
    const uint NbdErrorNotSupported = 95;
    const int NbdRequestBytes = 28;
    const int NbdHandshakeBytes = 152;
    const int NbdReplyBytes = 16;
    const int NbdReadCommand = 0;
    const int NbdWriteCommand = 1;
    const int NbdDisconnectCommand = 2;
    const int NbdFlushCommand = 3;

    internal async Task RunAsync(CancellationToken cancellationToken) {
        var tcpListener = new TcpListener(IPAddress.Any, Port);
        tcpListener.Start();
        logger.LogInformation("NBD listening on 0.0.0.0:{Port}", Port);

        try {
            while (true) {
                using var tcpClient = await tcpListener.AcceptTcpClientAsync(cancellationToken);
                tcpClient.NoDelay = true;
                logger.LogInformation("NBD client connected");

                try {
                    await HandleClientAsync(tcpClient, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException) {
                    logger.LogError(exception, "NBD client failed");
                }
            }
        }
        finally {
            tcpListener.Stop();
        }
    }

    async Task HandleClientAsync(TcpClient tcpClient, CancellationToken cancellationToken) {
        await using var networkStream = tcpClient.GetStream();
        await WriteHandshakeAsync(networkStream, cancellationToken);
        var requestBuffer = new byte[NbdRequestBytes];

        while (true) {
            if (!await ReadExactlyOrFalseAsync(networkStream, requestBuffer, cancellationToken))
                return;

            var magic = BinaryPrimitives.ReadUInt32BigEndian(requestBuffer);
            if (magic != NbdRequestMagic)
                throw new InvalidOperationException($"Invalid NBD request magic: 0x{magic:X8}");

            var commandType = BinaryPrimitives.ReadUInt32BigEndian(requestBuffer.AsSpan(4)) & 0xFFFF;
            var handle = requestBuffer.AsMemory(8, 8);
            var offset = checked((long)BinaryPrimitives.ReadUInt64BigEndian(requestBuffer.AsSpan(16)));
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(requestBuffer.AsSpan(24)));

            if (length > DiskConstants.MaxFileSizeBytes) {
                await WriteReplyAsync(networkStream, handle, NbdErrorInvalidArgument, cancellationToken);
                return;
            }

            switch (commandType) {
                case NbdReadCommand:
                    var readData = new byte[length];
                    await diskService.ReadAsync(offset, readData, cancellationToken);
                    await WriteReplyAsync(networkStream, handle, 0, cancellationToken);
                    await networkStream.WriteAsync(readData, cancellationToken);
                    break;

                case NbdWriteCommand:
                    var writeData = new byte[length];
                    await ReadExactlyAsync(networkStream, writeData, cancellationToken);
                    await diskService.WriteAsync(offset, writeData, cancellationToken);
                    await WriteReplyAsync(networkStream, handle, 0, cancellationToken);
                    break;

                case NbdDisconnectCommand:
                    await diskService.SaveAsync(cancellationToken);
                    return;

                case NbdFlushCommand:
                    await diskService.SaveAsync(cancellationToken);
                    await WriteReplyAsync(networkStream, handle, 0, cancellationToken);
                    break;

                default:
                    await WriteReplyAsync(networkStream, handle, NbdErrorNotSupported, cancellationToken);
                    break;
            }
        }
    }

    static async Task WriteHandshakeAsync(NetworkStream networkStream, CancellationToken cancellationToken) {
        var handshakeBuffer = new byte[NbdHandshakeBytes];
        BinaryPrimitives.WriteUInt64BigEndian(handshakeBuffer, NbdMagic);
        BinaryPrimitives.WriteUInt64BigEndian(handshakeBuffer.AsSpan(8), NbdOldStyleMagic);
        BinaryPrimitives.WriteUInt64BigEndian(handshakeBuffer.AsSpan(16), unchecked((ulong)DiskConstants.VirtualDiskSizeBytes));
        await networkStream.WriteAsync(handshakeBuffer, cancellationToken);
    }

    static async Task WriteReplyAsync(NetworkStream networkStream, ReadOnlyMemory<byte> handle, uint error, CancellationToken cancellationToken) {
        var replyBuffer = new byte[NbdReplyBytes];
        BinaryPrimitives.WriteUInt32BigEndian(replyBuffer, NbdReplyMagic);
        BinaryPrimitives.WriteUInt32BigEndian(replyBuffer.AsSpan(4), error);
        handle.CopyTo(replyBuffer.AsMemory(8));
        await networkStream.WriteAsync(replyBuffer, cancellationToken);
    }

    static async Task<bool> ReadExactlyOrFalseAsync(NetworkStream networkStream, Memory<byte> buffer, CancellationToken cancellationToken) {
        return await networkStream.ReadAtLeastAsync(buffer, buffer.Length, false, cancellationToken) == buffer.Length;
    }

    static async Task ReadExactlyAsync(NetworkStream networkStream, Memory<byte> buffer, CancellationToken cancellationToken) {
        if (!await ReadExactlyOrFalseAsync(networkStream, buffer, cancellationToken))
            throw new EndOfStreamException();
    }
}
