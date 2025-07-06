using LucHeart.WebsocketLibrary.Reconnection;

namespace Example;

public sealed class ExampleReconnectionPolicy : IReconnectPolicy
{
    public TimeSpan NextReconnectionDelay(ReconnectionContext reconnectionContext)
    {
        // Example: Exponential backoff with a maximum delay of 30 seconds
        var maxDelay = TimeSpan.FromSeconds(30);
        var delay = TimeSpan.FromSeconds(Math.Pow(2, reconnectionContext.Attempt));

        // Ensure the delay does not exceed the maximum
        return delay > maxDelay ? maxDelay : delay;
    }
}