using Microsoft.Extensions.Hosting;
using TeleDisk;

var hostApplicationBuilder = Host.CreateApplicationBuilder(args);
hostApplicationBuilder.Services.AddTeleDisk();
using var host = hostApplicationBuilder.Build();
await host.RunAsync();
