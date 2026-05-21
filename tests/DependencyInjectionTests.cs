using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace TeleDisk.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddTeleDisk_ShouldThrow_WhenTokenMissing() => WithServices(services =>
    {
        var act = () => services.AddTeleDisk((string?)null);
        act.Should().Throw<InvalidOperationException>().WithMessage("*TELEGRAM_BOT_TOKEN*");
    });

    [Fact]
    public void AddTeleDisk_ShouldRegisterCoreServices_WhenTokenExists() => WithProvider("123456:valid_token_value", provider =>
    {
        provider.GetRequiredService<Application.VirtualDiskService>().Should().NotBeNull();
        provider.GetRequiredService<Transport.Nbd.NbdEndpoint>().Should().NotBeNull();
    });

    [Fact]
    public void AddTeleDisk_ShouldRegisterBotTokenSingleton() => WithProvider("123456:expected_token_value", provider =>
    {
        provider.GetRequiredService<global::TeleDisk.Infrastructure.Telegram.TelegramBotToken>()
            .Value
            .Should()
            .Be("123456:expected_token_value");
    });

    private static void WithProvider(string token, Action<ServiceProvider> assert)
    {
        WithServices(services =>
        {
            services.AddTeleDisk(token);
            using var provider = services.BuildServiceProvider();
            assert(provider);
        });
    }

    private static void WithServices(Action<ServiceCollection> assert)
    {
        var services = new ServiceCollection();
        assert(services);
    }
}
