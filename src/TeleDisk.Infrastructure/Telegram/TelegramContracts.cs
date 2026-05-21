using System.Text.Json.Serialization;

namespace TeleDisk.Infrastructure.Telegram;

internal sealed record TelegramBotToken(string Value);


internal sealed record TelegramBotDescription([property: JsonPropertyName("description")] string Description);


internal sealed record TelegramApiResponse<T>(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("result")] T? Result,
    [property: JsonPropertyName("description")] string? Description);
