using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace TeleDisk.Telegram;

internal sealed class TelegramStorage(TelegramBotClient telegramBotClient, IHttpClientFactory httpClientFactory, TelegramBotToken telegramBotToken) {
    const string StorageChatId = "@CauntHermesBot";
    const string IndexDescriptionPrefix = "tg-nbd-index:";

    readonly HttpClient _httpClient = httpClientFactory.CreateClient(nameof(TelegramStorage));

    internal async Task<string?> GetIndexFileIdAsync(CancellationToken cancellationToken) {
        var description = await GetBotDescriptionAsync(cancellationToken);
        return description.StartsWith(IndexDescriptionPrefix, StringComparison.Ordinal) ? description[IndexDescriptionPrefix.Length..].Trim() : null;
    }

    internal async Task SetIndexFileIdAsync(string fileId, CancellationToken cancellationToken) {
        await SetBotDescriptionAsync($"{IndexDescriptionPrefix}{fileId}", cancellationToken);
    }

    internal async Task<string> UploadFileAsync(byte[] bytes, string fileName, CancellationToken cancellationToken) {
        await using var stream = new MemoryStream(bytes);
        var message = await telegramBotClient.SendDocument(StorageChatId, InputFile.FromStream(stream, fileName), cancellationToken: cancellationToken);
        return message.Document?.FileId ?? throw new InvalidOperationException("Telegram returned message without document.");
    }

    internal async Task<byte[]> DownloadFileAsync(string fileId, CancellationToken cancellationToken) {
        await using var stream = new MemoryStream();
        await telegramBotClient.GetInfoAndDownloadFile(fileId, stream, cancellationToken);
        return stream.ToArray();
    }

    async Task<string> GetBotDescriptionAsync(CancellationToken cancellationToken) {
        return (await PostTelegramAsync<TelegramBotDescription>("getMyDescription", [], cancellationToken)).Description;
    }

    async Task SetBotDescriptionAsync(string description, CancellationToken cancellationToken) {
        _ = await PostTelegramAsync<bool>("setMyDescription", [new("description", description)], cancellationToken);
    }

    async Task<T> PostTelegramAsync<T>(string method, IEnumerable<KeyValuePair<string, string>> fields, CancellationToken cancellationToken) {
        using var response = await _httpClient.PostAsync($"https://api.telegram.org/bot{telegramBotToken.Value}/{method}", new FormUrlEncodedContent(fields), cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var apiResponse = JsonSerializer.Deserialize<TelegramApiResponse<T>>(text);
        return apiResponse is { Ok: true, Result: not null } ? apiResponse.Result : throw new InvalidOperationException(text);
    }
}
