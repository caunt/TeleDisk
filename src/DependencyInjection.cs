using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using TeleDisk.Disk;
using TeleDisk.Nbd;
using TeleDisk.Telegram;

namespace TeleDisk;

internal static class DependencyInjection {
    internal static IServiceCollection AddTeleDisk(this IServiceCollection serviceCollection) {
        var botToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(botToken))
            throw new InvalidOperationException("TELEGRAM_BOT_TOKEN is not set.");

        serviceCollection.AddSingleton(new TelegramBotToken(botToken));
        serviceCollection.AddSingleton<TelegramBotClient>(serviceProvider => new TelegramBotClient(serviceProvider.GetRequiredService<TelegramBotToken>().Value));
        serviceCollection.AddHttpClient(nameof(TelegramStorage));
        serviceCollection.AddSingleton<TelegramStorage>();
        serviceCollection.AddSingleton<DiskService>();
        serviceCollection.AddSingleton<NbdServer>();
        serviceCollection.AddHostedService<TeleDiskHostedService>();
        return serviceCollection;
    }
}
