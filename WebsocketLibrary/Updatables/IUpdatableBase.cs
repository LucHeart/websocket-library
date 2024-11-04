namespace LucHeart.WebsocketLibrary.Updatables;

public interface IUpdatableBase<out T>
{
    public T Value { get; }
}