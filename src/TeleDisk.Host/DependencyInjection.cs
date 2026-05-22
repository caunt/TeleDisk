using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using TeleDisk.Application;
using TeleDisk.Infrastructure.Telegram;
using TeleDisk.Transport.Nbd;

namespace TeleDisk;

internal static class DependencyInjection
{
    private const char BotTokenSeparator = ',';

    internal static IServiceCollection AddTeleDisk(this IServiceCollection serviceCollection)
    {
        var rawBotTokens = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        AddTeleDisk(serviceCollection, rawBotTokens);
        return serviceCollection;
    }

    internal static IServiceCollection AddTeleDisk(this IServiceCollection serviceCollection, string? rawBotTokens)
    {
        var botTokens = ParseBotTokens(rawBotTokens);
        serviceCollection.AddSingleton(new TelegramBotTokenCatalog(botTokens));
        serviceCollection.AddHttpClient(nameof(TelegramBlobStore));
        serviceCollection.AddSingleton<ExportRegistry>();
        serviceCollection.AddScoped<global::TeleDisk.Transport.Nbd.ClientExportSession>();
        serviceCollection.AddSingleton<global::TeleDisk.Transport.Nbd.IExportRegistry>(serviceProvider => serviceProvider.GetRequiredService<ExportRegistry>());
        serviceCollection.AddSingleton<NbdEndpoint>();
        serviceCollection.AddHostedService<TeleDiskHostedService>();
        return serviceCollection;
    }

    private static IReadOnlyList<TelegramBotToken> ParseBotTokens(string? rawBotTokens)
    {
        if (string.IsNullOrWhiteSpace(rawBotTokens))
        {
            throw new InvalidOperationException("TELEGRAM_BOT_TOKEN is not set.");
        }

        var botTokens = rawBotTokens
            .Split(BotTokenSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => new TelegramBotToken(value))
            .ToArray();

        if (botTokens.Length == 0)
        {
            throw new InvalidOperationException("TELEGRAM_BOT_TOKEN is not set.");
        }

        return botTokens;
    }
}
