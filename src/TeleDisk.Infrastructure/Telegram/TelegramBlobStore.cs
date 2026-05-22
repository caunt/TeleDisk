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
        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            destination.Span.Clear();
            return;
        }

        response.EnsureSuccessStatusCode();
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytesRead = await responseStream.ReadAtLeastAsync(destination, destination.Length, false, cancellationToken);
        if (bytesRead < destination.Length)
        {
            destination[bytesRead..].Span.Clear();
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
        if (apiResponse is { Ok: true, Result: not null })
        {
            return apiResponse.Result;
        }

        var retryAfter = TryGetRetryAfterSeconds(body);
        if (retryAfter is { } retryDelay)
        {
            throw new TelegramRateLimitException(retryDelay, body);
        }

        throw new InvalidOperationException(body);
    }

    private static TimeSpan? TryGetRetryAfterSeconds(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("parameters", out var parameters) ||
            !parameters.TryGetProperty("retry_after", out var retryAfterElement) ||
            !retryAfterElement.TryGetInt32(out var retryAfterSeconds) ||
            retryAfterSeconds <= 0)
        {
            return null;
        }

        return TimeSpan.FromSeconds(retryAfterSeconds);
    }
}

public sealed class TelegramRateLimitException(TimeSpan retryAfter, string message) : InvalidOperationException(message)
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}
