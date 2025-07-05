using System.Text.Json;
using LucHeart.WebsocketLibrary.Reconnection;
using Microsoft.Extensions.Logging;
using OneOf;
using OneOf.Types;

namespace LucHeart.WebsocketLibrary;

public sealed class WebsocketClientOptions
{
    public ILogger? Logger { get; set; } = null;
    public JsonSerializerOptions? JsonSerializerOptions { get; set; } = null;
    public IDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
    public IReconnectPolicy ReconnectPolicy { get; set; } = new DefaultReconnectPolicy();
}

public readonly struct WebsocketConnectOptions
{
    public Uri Uri { get; init; }
}

public delegate Task<OneOf<WebsocketConnectOptions, Error>> WebsocketConnectHook();