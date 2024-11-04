using LucHeart.WebsocketLibrary.Utils;

namespace LucHeart.WebsocketLibrary.Updatables;

public sealed class AsyncUpdatableVariable<T>(T internalValue) : IAsyncUpdatable<T>
{
    private T _internalValue = internalValue;

    public T Value
    {
        get => _internalValue;
        set
        {
            if (_internalValue!.Equals(value)) return;
            _internalValue = value;
            Task.Run(() => OnValueChanged?.Raise(value));
        }
    }
    
    public event Func<T, Task>? OnValueChanged;
    
    public void UpdateWithoutNotify(T newValue)
    {
        _internalValue = newValue;
    }
}