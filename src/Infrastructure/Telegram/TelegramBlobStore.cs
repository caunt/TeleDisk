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

    internal async Task<string> UploadFileAsync(byte[] bytes, string fileName, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(bytes);
        var message = await telegramBotClient.SendDocument(StorageChatId, InputFile.FromStream(stream, fileName), cancellationToken: cancellationToken);
        return message.Document?.FileId ?? throw new InvalidOperationException("Telegram returned message without document.");
    }

    internal async Task<byte[]> DownloadFileAsync(string fileId, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream();
        await telegramBotClient.GetInfoAndDownloadFile(fileId, stream, cancellationToken);
        return stream.ToArray();
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
