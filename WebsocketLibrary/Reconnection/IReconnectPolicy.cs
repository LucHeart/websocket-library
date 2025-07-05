namespace LucHeart.WebsocketLibrary.Reconnection;

public interface IReconnectPolicy
{
    public TimeSpan NextReconnectionDelay(ReconnectionContext reconnectionContext);
}

public sealed class ReconnectionContext
{
    public int Attempt { get; internal set; }
}