namespace LucHeart.WebsocketLibrary;

public enum WebsocketConnectionState
{
    NotStarted = 0,
    Connecting = 1,
    Connected = 2,
    WaitingForReconnect = 3,
    Disconnected = 4,
}