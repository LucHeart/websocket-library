using OpenShock.MinimalEvents;

namespace LucHeart.WebsocketLibrary.Updatables;

public interface IAsyncUpdatable<out T>
{
    public T Value { get; }
    public IAsyncMinimalEventObservable<T> Updated { get; }
}