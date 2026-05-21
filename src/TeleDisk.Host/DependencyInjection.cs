using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using TeleDisk.Application;
using TeleDisk.Infrastructure.Telegram;
using TeleDisk.Transport.Nbd;

namespace TeleDisk;

internal static class DependencyInjection
{
    internal static IServiceCollection AddTeleDisk(this IServiceCollection serviceCollection)
    {
        var botToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        AddTeleDisk(serviceCollection, botToken);
        return serviceCollection;
    }

    internal static IServiceCollection AddTeleDisk(this IServiceCollection serviceCollection, string? botToken)
    {
        if (string.IsNullOrWhiteSpace(botToken))
        {
            throw new InvalidOperationException("TELEGRAM_BOT_TOKEN is not set.");
        }

        serviceCollection.AddSingleton(new TelegramBotToken(botToken));
        serviceCollection.AddSingleton(serviceProvider => new TelegramBotClient(serviceProvider.GetRequiredService<TelegramBotToken>().Value));
        serviceCollection.AddHttpClient(nameof(TelegramBlobStore));
        serviceCollection.AddSingleton<TelegramBlobStore>();
        serviceCollection.AddSingleton<VirtualDiskService>();
        serviceCollection.AddSingleton<NbdEndpoint>();
        serviceCollection.AddHostedService<TeleDiskHostedService>();
        return serviceCollection;
    }
}
