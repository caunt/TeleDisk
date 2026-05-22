using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using TeleDisk.Application;
using TeleDisk.Infrastructure.Telegram;
using TeleDisk.Transport.Nbd;

namespace TeleDisk;

internal sealed class ExportRegistry(TelegramBotTokenCatalog botTokenCatalog, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory) : IExportRegistry
{
    private readonly ConcurrentDictionary<string, VirtualDiskService> _exports = new(StringComparer.Ordinal);

    public VirtualDiskService Resolve(string? exportName)
    {
        var token = ResolveToken(exportName);
        return _exports.GetOrAdd(token.Value, value =>
        {
            var telegramBotToken = new TelegramBotToken(value);
            var botClient = new TelegramBotClient(telegramBotToken.Value);
            var blobStore = new TelegramBlobStore(botClient, httpClientFactory, telegramBotToken);
            return new VirtualDiskService(blobStore, loggerFactory.CreateLogger<ChunkedVirtualDisk>());
        });
    }

    public IReadOnlyList<string> GetExportNames() =>
        botTokenCatalog.Tokens.Count == 1
            ? ["teledisk"]
            : [.. botTokenCatalog.Tokens.Select((_, index) => $"teledisk-{index + 1}")];

    private TelegramBotToken ResolveToken(string? exportName)
    {
        if (botTokenCatalog.Tokens.Count == 1)
        {
            return botTokenCatalog.DefaultToken;
        }

        if (!string.IsNullOrWhiteSpace(exportName) && exportName.StartsWith("teledisk-", StringComparison.Ordinal))
        {
            var suffix = exportName["teledisk-".Length..];
            if (int.TryParse(suffix, out var exportIndex) && exportIndex > 0 && exportIndex <= botTokenCatalog.Tokens.Count)
            {
                return botTokenCatalog.Tokens[exportIndex - 1];
            }
        }

        return botTokenCatalog.DefaultToken;
    }
}
