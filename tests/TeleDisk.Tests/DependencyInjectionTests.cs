using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace TeleDisk.Tests;

public sealed class DependencyInjectionTests
{
    private const string TokenVariable = "TELEGRAM_BOT_TOKEN";

    [Fact]
    public void AddTeleDisk_ShouldThrow_WhenTokenMissing()
    {
        var previousToken = Environment.GetEnvironmentVariable(TokenVariable);
        Environment.SetEnvironmentVariable(TokenVariable, null);
        var services = new ServiceCollection();

        var act = () => services.AddTeleDisk();

        try
        {
            act.Should().Throw<InvalidOperationException>().WithMessage("*TELEGRAM_BOT_TOKEN*");
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, previousToken);
        }
    }

    [Fact]
    public void AddTeleDisk_ShouldRegisterCoreServices_WhenTokenExists()
    {
        var previousToken = Environment.GetEnvironmentVariable(TokenVariable);
        Environment.SetEnvironmentVariable(TokenVariable, "123456:valid_token_value");
        var services = new ServiceCollection();

        try
        {
            services.AddTeleDisk();
            var provider = services.BuildServiceProvider();

            provider.GetRequiredService<Application.VirtualDiskService>().Should().NotBeNull();
            provider.GetRequiredService<Transport.Nbd.NbdEndpoint>().Should().NotBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, previousToken);
        }
    }

    [Fact]
    public void AddTeleDisk_ShouldRegisterBotTokenSingleton()
    {
        var previousToken = Environment.GetEnvironmentVariable(TokenVariable);
        Environment.SetEnvironmentVariable(TokenVariable, "123456:expected_token_value");
        var services = new ServiceCollection();

        try
        {
            services.AddTeleDisk();
            var provider = services.BuildServiceProvider();

            var botToken = provider.GetRequiredService<global::TeleDisk.Infrastructure.Telegram.TelegramBotToken>();
            botToken.Value.Should().Be("123456:expected_token_value");
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, previousToken);
        }
    }
}
