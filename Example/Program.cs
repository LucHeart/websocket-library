using LucHeart.WebsocketLibrary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

Host.CreateDefaultBuilder();var hostBuilder = Host.CreateApplicationBuilder();

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

var json = new JsonWebsocketClient<string, object>(new Uri("wss://echo.websocket.org"), new WebsocketClientOptions()
{
    Logger = loggerFactory.CreateLogger("JsonWebsocketClient"),
});
await json.OnMessage.SubscribeAsync(async s => Console.WriteLine(s));

Console.WriteLine("Starting WebSocket client...");
json.Start();

await json.QueueMessage("\"Hello, world!\"");

await app.RunAsync();