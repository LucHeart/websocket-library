namespace LucHeart.WebsocketLibrary.Reconnection;

public sealed class DefaultReconnectPolicy : IReconnectPolicy
{
    public TimeSpan NextReconnectionDelay(ReconnectionContext reconnectionContext)
    {
        return TimeSpan.FromSeconds(3);
    }
}