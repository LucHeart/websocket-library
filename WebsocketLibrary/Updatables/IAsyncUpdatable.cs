namespace LucHeart.WebsocketLibrary.Updatables;

public interface IAsyncUpdatable<out T> : IUpdatableBase<T>
{
    public event Func<T, Task>? OnValueChanged;
}