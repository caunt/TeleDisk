using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace TeleDisk.Tests;

public sealed class DependencyInjectionTests
{
    private const string TokenVariable = "TELEGRAM_BOT_TOKEN";

    [Fact]
    public void AddTeleDisk_ShouldThrow_WhenTokenMissing() => WithToken(null, services =>
    {
        var act = () => services.AddTeleDisk();
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
        WithToken(token, services =>
        {
            services.AddTeleDisk();
            using var provider = services.BuildServiceProvider();
            assert(provider);
        });
    }

    private static void WithToken(string? token, Action<ServiceCollection> assert)
    {
        var previousToken = Environment.GetEnvironmentVariable(TokenVariable);
        Environment.SetEnvironmentVariable(TokenVariable, token);
        var services = new ServiceCollection();

        try
        {
            assert(services);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, previousToken);
        }
    }
}
