using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Telegram.Bot;
using Telegram.Bot.Types;

const int NbdPort = 10809;

var botToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");

if (string.IsNullOrWhiteSpace(botToken))
    throw new InvalidOperationException("TELEGRAM_BOT_TOKEN is not set.");

using var httpClient = new HttpClient();
using var cancellationTokenSource = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArguments) =>
{
    eventArguments.Cancel = true;
    cancellationTokenSource.Cancel();
};

var telegramStore = new TelegramStore(new TelegramBotClient(botToken), httpClient, botToken);
var disk = await TelegramBackedDisk.LoadAsync(telegramStore, cancellationTokenSource.Token);

_ = SavePeriodicallyAsync();

try
{
    await new NbdServer(disk, NbdPort).RunAsync(cancellationTokenSource.Token);
}
catch (OperationCanceledException)
{
}
finally
{
    await disk.SaveAsync(CancellationToken.None);
}

async Task SavePeriodicallyAsync()
{
    try
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationTokenSource.Token);
            await disk.SaveAsync(cancellationTokenSource.Token);
        }
    }
    catch (OperationCanceledException)
    {
    }
}

sealed class NbdServer(TelegramBackedDisk disk, int port)
{
    const ulong NbdMagic = 0x4e42444d41474943;
    const ulong NbdOldStyleMagic = 0x0000420281861253;
    const uint NbdRequestMagic = 0x25609513;
    const uint NbdReplyMagic = 0x67446698;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();

        Console.WriteLine($"NBD listening on 0.0.0.0:{port}");

        try
        {
            while (true)
            {
                using var tcpClient = await listener.AcceptTcpClientAsync(cancellationToken);
                tcpClient.NoDelay = true;

                Console.WriteLine("NBD client connected");

                try
                {
                    await HandleClientAsync(tcpClient, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Console.WriteLine(exception);
                }
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    async Task HandleClientAsync(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        await using var stream = tcpClient.GetStream();

        await WriteHandshakeAsync(stream, cancellationToken);

        var requestBuffer = new byte[28];

        while (true)
        {
            if (!await ReadExactlyOrFalseAsync(stream, requestBuffer, cancellationToken))
                return;

            var magic = BinaryPrimitives.ReadUInt32BigEndian(requestBuffer);

            if (magic != NbdRequestMagic)
                throw new InvalidOperationException($"Invalid NBD request magic: 0x{magic:X8}");

            var commandType = BinaryPrimitives.ReadUInt32BigEndian(requestBuffer.AsSpan(4)) & 0xFFFF;
            var handle = requestBuffer.AsMemory(8, 8);
            var offset = checked((long)BinaryPrimitives.ReadUInt64BigEndian(requestBuffer.AsSpan(16)));
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(requestBuffer.AsSpan(24)));

            if (length > TelegramNbdConstants.TelegramHostedBotApiMaxDownloadFileSizeBytes)
            {
                await WriteReplyAsync(stream, handle, 22, cancellationToken);
                return;
            }

            switch (commandType)
            {
                case 0:
                    var readData = new byte[length];

                    await disk.ReadAsync(offset, readData, cancellationToken);
                    await WriteReplyAsync(stream, handle, 0, cancellationToken);
                    await stream.WriteAsync(readData, cancellationToken);
                    break;

                case 1:
                    var writeData = new byte[length];

                    await ReadExactlyAsync(stream, writeData, cancellationToken);
                    await disk.WriteAsync(offset, writeData, cancellationToken);
                    await WriteReplyAsync(stream, handle, 0, cancellationToken);
                    break;

                case 2:
                    await disk.SaveAsync(cancellationToken);
                    return;

                case 3:
                    await disk.SaveAsync(cancellationToken);
                    await WriteReplyAsync(stream, handle, 0, cancellationToken);
                    break;

                default:
                    await WriteReplyAsync(stream, handle, 95, cancellationToken);
                    break;
            }
        }
    }

    static async Task WriteHandshakeAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[152];

        BinaryPrimitives.WriteUInt64BigEndian(buffer, NbdMagic);
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(8), NbdOldStyleMagic);
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(16), unchecked((ulong)TelegramNbdConstants.VirtualDiskSizeBytes));

        await stream.WriteAsync(buffer, cancellationToken);
    }

    static async Task WriteReplyAsync(NetworkStream stream, ReadOnlyMemory<byte> handle, uint error, CancellationToken cancellationToken)
    {
        var buffer = new byte[16];

        BinaryPrimitives.WriteUInt32BigEndian(buffer, NbdReplyMagic);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4), error);
        handle.CopyTo(buffer.AsMemory(8));

        await stream.WriteAsync(buffer, cancellationToken);
    }

    static async Task<bool> ReadExactlyOrFalseAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        return await stream.ReadAtLeastAsync(buffer, buffer.Length, false, cancellationToken) == buffer.Length;
    }

    static async Task ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (!await ReadExactlyOrFalseAsync(stream, buffer, cancellationToken))
            throw new EndOfStreamException();
    }
}

sealed class TelegramBackedDisk(TelegramStore telegramStore, TelegramNbdIndex index)
{
    readonly Dictionary<long, byte[]> chunkCache = [];
    readonly HashSet<long> dirtyChunkIndexes = [];
    readonly SemaphoreSlim semaphore = new(1, 1);

    public static async Task<TelegramBackedDisk> LoadAsync(TelegramStore telegramStore, CancellationToken cancellationToken)
    {
        var indexFileId = await telegramStore.GetIndexFileIdAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(indexFileId))
            return new TelegramBackedDisk(telegramStore, new TelegramNbdIndex(TelegramNbdConstants.VirtualDiskSizeBytes, TelegramNbdConstants.TelegramChunkSizeBytes, []));

        var index = JsonSerializer.Deserialize<TelegramNbdIndex>(await telegramStore.DownloadFileAsync(indexFileId, cancellationToken));

        return new TelegramBackedDisk(telegramStore, index ?? throw new InvalidOperationException("Invalid Telegram NBD index."));
    }

    public async Task ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            ValidateRange(offset, destination.Length);

            for (var destinationOffset = 0; destinationOffset < destination.Length;)
            {
                var chunkIndex = offset / index.ChunkSizeBytes;
                var chunkOffset = (int)(offset % index.ChunkSizeBytes);
                var bytesToCopy = Math.Min(destination.Length - destinationOffset, index.ChunkSizeBytes - chunkOffset);
                var chunkBytes = await GetChunkAsync(chunkIndex, false, cancellationToken);

                if (chunkBytes is null)
                    destination.Slice(destinationOffset, bytesToCopy).Span.Clear();
                else
                    chunkBytes.AsMemory(chunkOffset, bytesToCopy).CopyTo(destination.Slice(destinationOffset, bytesToCopy));

                offset += bytesToCopy;
                destinationOffset += bytesToCopy;
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            ValidateRange(offset, source.Length);

            for (var sourceOffset = 0; sourceOffset < source.Length;)
            {
                var chunkIndex = offset / index.ChunkSizeBytes;
                var chunkOffset = (int)(offset % index.ChunkSizeBytes);
                var bytesToCopy = Math.Min(source.Length - sourceOffset, index.ChunkSizeBytes - chunkOffset);
                var chunkBytes = await GetChunkAsync(chunkIndex, true, cancellationToken) ?? throw new InvalidOperationException();

                source.Slice(sourceOffset, bytesToCopy).CopyTo(chunkBytes.AsMemory(chunkOffset, bytesToCopy));
                dirtyChunkIndexes.Add(chunkIndex);

                offset += bytesToCopy;
                sourceOffset += bytesToCopy;
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            foreach (var chunkIndex in dirtyChunkIndexes.ToArray())
            {
                var chunkBytes = chunkCache[chunkIndex];
                var fileId = await telegramStore.UploadFileAsync(chunkBytes, $"chunk-{chunkIndex}.bin", cancellationToken);

                index.Chunks[chunkIndex] = new TelegramNbdChunk(fileId, Convert.ToHexString(SHA256.HashData(chunkBytes)));
                dirtyChunkIndexes.Remove(chunkIndex);

                Console.WriteLine($"Saved chunk {chunkIndex}");
            }

            var indexFileId = await telegramStore.UploadFileAsync(JsonSerializer.SerializeToUtf8Bytes(index), "telegram-nbd-index.json", cancellationToken);

            await telegramStore.SetIndexFileIdAsync(indexFileId, cancellationToken);

            Console.WriteLine($"Saved index {indexFileId}");
        }
        finally
        {
            semaphore.Release();
        }
    }

    async Task<byte[]?> GetChunkAsync(long chunkIndex, bool create, CancellationToken cancellationToken)
    {
        if (chunkCache.TryGetValue(chunkIndex, out var chunkBytes))
            return chunkBytes;

        if (!index.Chunks.TryGetValue(chunkIndex, out var chunkInfo))
            return create ? chunkCache[chunkIndex] = new byte[index.ChunkSizeBytes] : null;

        chunkBytes = await telegramStore.DownloadFileAsync(chunkInfo.FileId, cancellationToken);

        if (chunkBytes.Length != index.ChunkSizeBytes)
            Array.Resize(ref chunkBytes, index.ChunkSizeBytes);

        return chunkCache[chunkIndex] = chunkBytes;
    }

    static void ValidateRange(long offset, int length)
    {
        if (offset < 0 || length < 0 || offset > TelegramNbdConstants.VirtualDiskSizeBytes - length)
            throw new ArgumentOutOfRangeException(nameof(offset));
    }
}

sealed class TelegramStore(TelegramBotClient botClient, HttpClient httpClient, string botToken)
{
    public async Task<string?> GetIndexFileIdAsync(CancellationToken cancellationToken)
    {
        var description = await GetBotDescriptionAsync(cancellationToken);
        var prefix = TelegramNbdConstants.TelegramIndexDescriptionPrefix;

        return description.StartsWith(prefix, StringComparison.Ordinal) ? description[prefix.Length..].Trim() : null;
    }

    public async Task SetIndexFileIdAsync(string fileId, CancellationToken cancellationToken)
    {
        await SetBotDescriptionAsync($"{TelegramNbdConstants.TelegramIndexDescriptionPrefix}{fileId}", cancellationToken);
    }

    public async Task<string> UploadFileAsync(byte[] bytes, string fileName, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(bytes);

        var message = await botClient.SendDocument(TelegramNbdConstants.TelegramStorageChatId, InputFile.FromStream(stream, fileName), cancellationToken: cancellationToken);

        return message.Document?.FileId ?? throw new InvalidOperationException("Telegram returned message without document.");
    }

    public async Task<byte[]> DownloadFileAsync(string fileId, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream();

        await botClient.GetInfoAndDownloadFile(fileId, stream, cancellationToken);

        return stream.ToArray();
    }

    async Task<string> GetBotDescriptionAsync(CancellationToken cancellationToken)
    {
        return (await PostTelegramAsync<TelegramBotDescription>("getMyDescription", [], cancellationToken)).Description;
    }

    async Task SetBotDescriptionAsync(string description, CancellationToken cancellationToken)
    {
        _ = await PostTelegramAsync<bool>("setMyDescription", [new("description", description)], cancellationToken);
    }

    async Task<T> PostTelegramAsync<T>(string method, IEnumerable<KeyValuePair<string, string>> fields, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"https://api.telegram.org/bot{botToken}/{method}", new FormUrlEncodedContent(fields), cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var apiResponse = JsonSerializer.Deserialize<TelegramApiResponse<T>>(text);

        return apiResponse is { Ok: true, Result: not null } ? apiResponse.Result : throw new InvalidOperationException(text);
    }
}

static class TelegramNbdConstants
{
    public const string TelegramStorageChatId = "@CauntHermesBot";
    public const string TelegramIndexDescriptionPrefix = "tg-nbd-index:";
    public const int TelegramHostedBotApiMaxDownloadFileSizeBytes = 20 * 1024 * 1024;
    public const int TelegramChunkSizeBytes = 4 * 1024 * 1024;
    public const long VirtualDiskSizeBytes = 1024L * 1024 * 1024;
}

sealed record TelegramNbdIndex(long DiskSizeBytes, int ChunkSizeBytes, Dictionary<long, TelegramNbdChunk> Chunks);
sealed record TelegramNbdChunk(string FileId, string Sha256);
sealed record TelegramBotDescription([property: JsonPropertyName("description")] string Description);
sealed record TelegramApiResponse<T>([property: JsonPropertyName("ok")] bool Ok, [property: JsonPropertyName("result")] T? Result, [property: JsonPropertyName("description")] string? Description);
