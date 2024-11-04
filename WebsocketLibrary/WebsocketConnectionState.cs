namespace LucHeart.WebsocketLibrary;

public enum WebsocketConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3
}