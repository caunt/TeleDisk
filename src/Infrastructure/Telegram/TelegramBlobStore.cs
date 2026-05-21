using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace TeleDisk.Infrastructure.Telegram;

internal sealed class TelegramBlobStore(TelegramBotClient telegramBotClient, IHttpClientFactory httpClientFactory, TelegramBotToken telegramBotToken)
{
    private const string StorageChatId = "@CauntHermesBot";
    private const string IndexDescriptionPrefix = "tg-nbd-index:";
    private readonly HttpClient _httpClient = httpClientFactory.CreateClient(nameof(TelegramBlobStore));

    internal async Task<string?> GetIndexFileIdAsync(CancellationToken cancellationToken)
    {
        var description = await GetBotDescriptionAsync(cancellationToken);
        return description.StartsWith(IndexDescriptionPrefix, StringComparison.Ordinal)
            ? description[IndexDescriptionPrefix.Length..].Trim()
            : null;
    }

    internal Task SetIndexFileIdAsync(string fileId, CancellationToken cancellationToken) =>
        SetBotDescriptionAsync($"{IndexDescriptionPrefix}{fileId}", cancellationToken);

    internal async Task<string> UploadFileAsync(ReadOnlyMemory<byte> payload, string fileName, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(payload.ToArray(), writable: false);
        var message = await telegramBotClient.SendDocument(StorageChatId, InputFile.FromStream(stream, fileName), cancellationToken: cancellationToken);
        return message.Document?.FileId ?? throw new InvalidOperationException("Telegram returned message without document.");
    }

    internal async Task<byte[]> DownloadChunkOrZeroAsync(string? fileId, int chunkSizeBytes, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileId))
        {
            return new byte[chunkSizeBytes];
        }

        var destination = new byte[chunkSizeBytes];
        await DownloadRangeAsync(fileId, 0, destination, cancellationToken);
        return destination;
    }

    internal async Task DownloadRangeAsync(string fileId, int offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var file = await telegramBotClient.GetFile(fileId, cancellationToken);
        var fileUri = $"https://api.telegram.org/file/bot{telegramBotToken.Value}/{file.FilePath}";
        using var request = new HttpRequestMessage(HttpMethod.Get, fileUri);
        request.Headers.Range = new(offset, offset + destination.Length - 1);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        if (await responseStream.ReadAtLeastAsync(destination, destination.Length, false, cancellationToken) != destination.Length)
        {
            throw new InvalidOperationException("Unexpected file size.");
        }
    }

    internal async Task<T?> DownloadJsonAsync<T>(string fileId, CancellationToken cancellationToken)
    {
        var file = await telegramBotClient.GetFile(fileId, cancellationToken);
        await using var stream = await _httpClient.GetStreamAsync($"https://api.telegram.org/file/bot{telegramBotToken.Value}/{file.FilePath}", cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken);
    }

    internal async Task<string> UploadJsonAsync<T>(T payload, string fileName, CancellationToken cancellationToken)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(payload);
        return await UploadFileAsync(content, fileName, cancellationToken);
    }

    private async Task<string> GetBotDescriptionAsync(CancellationToken cancellationToken) =>
        (await PostTelegramAsync<TelegramBotDescription>("getMyDescription", [], cancellationToken)).Description;

    private async Task SetBotDescriptionAsync(string description, CancellationToken cancellationToken) =>
        _ = await PostTelegramAsync<bool>("setMyDescription", [new("description", description)], cancellationToken);

    private async Task<T> PostTelegramAsync<T>(string method, IEnumerable<KeyValuePair<string, string>> fields, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsync($"https://api.telegram.org/bot{telegramBotToken.Value}/{method}", new FormUrlEncodedContent(fields), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var apiResponse = JsonSerializer.Deserialize<TelegramApiResponse<T>>(body);
        return apiResponse is { Ok: true, Result: not null } ? apiResponse.Result : throw new InvalidOperationException(body);
    }
}
