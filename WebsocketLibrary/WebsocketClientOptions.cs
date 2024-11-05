using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LucHeart.WebsocketLibrary;

public sealed class WebsocketClientOptions
{
    public ILogger? Logger { get; set; } = null;
    public JsonSerializerOptions? JsonSerializerOptions { get; set; } = null;
    public IDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
}