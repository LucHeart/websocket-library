using Example;
using LucHeart.WebsocketLibrary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

var hostBuilder = Host.CreateApplicationBuilder();

var loggerConfiguration = new LoggerConfiguration()
    .MinimumLevel.Verbose()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Information)
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}");

Log.Logger = loggerConfiguration.CreateLogger();

hostBuilder.Logging.ClearProviders();
hostBuilder.Logging.AddSerilog();

var app = hostBuilder.Build();

var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

var json = new JsonWebsocketClient<string, object>(new Uri("ws://localhost"), new WebsocketClientOptions
{
    Logger = loggerFactory.CreateLogger("JsonWebsocketClient"),
    ReconnectPolicy = new ExampleReconnectionPolicy()
});
await json.OnMessage.SubscribeAsync(s =>
{
    Console.WriteLine(s);
    return Task.CompletedTask;
});

await json.State.Updated.SubscribeAsync(state =>
{
    Console.WriteLine(state);
    return Task.CompletedTask;
});


Console.WriteLine("Starting WebSocket client...");
json.Start();

// Any messages before connection of the client will be discarded
await Task.Delay(2000); // Wait for the client to connect

Console.WriteLine("Press Enter to send a message...");

while (true)
{
    Console.ReadLine();
    await json.QueueMessage($"\"Hello, world!\"{DateTime.UtcNow}");
}