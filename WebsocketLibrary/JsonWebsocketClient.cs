using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using LucHeart.WebsocketLibrary.Updatables;
using LucHeart.WebsocketLibrary.Utils;
using Microsoft.Extensions.Logging;
using OneOf.Types;

namespace LucHeart.WebsocketLibrary;

public sealed class JsonWebsocketClient<TRec, TSend> : IAsyncDisposable
{
    private readonly Uri _uri;
    private readonly JsonSerializerOptions _jsonSerializerOptions = JsonSerializerOptions.Default;

    private readonly ILogger? _logger;
    private ClientWebSocket? _clientWebSocket;
    
    private readonly CancellationTokenSource _dispose;
    private CancellationTokenSource _linked;
    private CancellationTokenSource? _currentConnectionClose;
    private readonly IDictionary<string, string> _headers = new Dictionary<string, string>();
    
    private Channel<TSend> _channel = Channel.CreateUnbounded<TSend>();

    public JsonWebsocketClient(Uri uri, WebsocketClientOptions? options = null)
    {
        _uri = uri;
        _logger = options?.Logger;
        if (options?.Headers != null) _headers = options.Headers;

        _dispose = new CancellationTokenSource();
        _linked = _dispose;
    }

    public ValueTask QueueMessage(TSend data) =>
        _channel.Writer.WriteAsync(data, _dispose.Token);

    private readonly AsyncUpdatableVariable<WebsocketConnectionState> _state =
        new(WebsocketConnectionState.Disconnected);

    public IAsyncUpdatable<WebsocketConnectionState> State => _state;

    public event Func<TRec, Task>? OnMessage; 
    public event Func<Task>? OnDispose;
    
    private async Task MessageLoop()
    {
        try
        {
            await foreach (var msg in _channel.Reader.ReadAllAsync(_linked.Token))
                await JsonWebSocketUtils.SendFullMessage(msg, _clientWebSocket!, _linked.Token, _jsonSerializerOptions);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Error in message loop");
        }
    }

    public struct Disposed;

    public struct Reconnecting;

    private async Task<OneOf.OneOf<Success, Reconnecting, Disposed>> ConnectAsync()
    {
        if (_dispose.IsCancellationRequested)
        {
            _logger?.LogWarning("Dispose requested, not connecting");
            return new Disposed();
        }

        _state.Value = WebsocketConnectionState.Connecting;
#if NETSTANDARD2_1
        _currentConnectionClose?.Cancel();
#else
        if (_currentConnectionClose != null) await _currentConnectionClose.CancelAsync();
#endif
        _currentConnectionClose = new CancellationTokenSource();
        _linked = CancellationTokenSource.CreateLinkedTokenSource(_dispose.Token, _currentConnectionClose.Token);

        _clientWebSocket?.Abort();
        _clientWebSocket?.Dispose();

        _channel = Channel.CreateUnbounded<TSend>();
        _clientWebSocket = new ClientWebSocket();
        
        foreach (var pair in _headers)
        {
            _clientWebSocket.Options.SetRequestHeader(pair.Key, pair.Value);
        }
        
        _logger?.LogInformation("Connecting to websocket....");
        try
        {
            await _clientWebSocket.ConnectAsync(_uri, _linked.Token);

            _logger?.LogInformation("Connected to websocket");
            _state.Value = WebsocketConnectionState.Connected;

#pragma warning disable CS4014
            Run(ReceiveLoop, _linked.Token);
            Run(MessageLoop, _linked.Token);
#pragma warning restore CS4014

            return new Success();
        }
        catch (WebSocketException e)
        {
            _logger?.LogError(e, "Error while connecting, retrying in 3 seconds");
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Error while connecting, retrying in 3 seconds");
        }

        _state.Value = WebsocketConnectionState.Reconnecting;
        _clientWebSocket.Abort();
        _clientWebSocket.Dispose();
        await Task.Delay(3000, _dispose.Token);
#pragma warning disable CS4014
        Run(ConnectAsync, _dispose.Token);
#pragma warning restore CS4014
        return new Reconnecting();
    }
    
    private async Task ReceiveLoop()
    {
        while (!_linked.Token.IsCancellationRequested)
        {
            try
            {
                if (_clientWebSocket!.State == WebSocketState.Aborted)
                {
                    _logger?.LogWarning("Websocket connection aborted, closing loop");
                    break;
                }

                var message =
                    await JsonWebSocketUtils
                        .ReceiveFullMessageAsyncNonAlloc<TRec>(_clientWebSocket, _linked.Token, _jsonSerializerOptions);

                if (message.IsT2)
                {
                    if (_clientWebSocket.State != WebSocketState.Open)
                    {
                        _logger?.LogWarning("Client sent closure, but connection state is not open");
                        break;
                    }

                    try
                    {
                        await _clientWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Normal close",
                            _linked.Token);
                    }
                    catch (OperationCanceledException e)
                    {
                        _logger?.LogError(e, "Error during close handshake");
                    }

                    _logger?.LogInformation("Closing websocket connection");
                    break;
                }

                message.Switch(wsMessage => { Run(OnMessage.Raise(wsMessage!)); },
                    failed =>
                    {
                        _logger?.LogWarning("Deserialization failed for websocket message [{Message}] \n\n {Exception}",
                            failed.Message,
                            failed.Exception);
                    },
                    _ => { });
            }
            catch (OperationCanceledException)
            {
                _logger?.LogInformation("WebSocket connection terminated due to close or shutdown");
                break;
            }
            catch (WebSocketException e)
            {
                if (e.WebSocketErrorCode != WebSocketError.ConnectionClosedPrematurely)
                    _logger?.LogError(e, "Error in receive loop, websocket exception");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Exception while processing websocket request");
            }
        }

#if NETSTANDARD2_1
        _currentConnectionClose?.Cancel();
#else
        await _currentConnectionClose!.CancelAsync();
#endif

        if (_dispose.IsCancellationRequested)
        {
            _logger?.LogDebug("Dispose requested, not reconnecting");
            return;
        }

        _logger?.LogWarning("Lost websocket connection, trying to reconnect in 3 seconds");
        _state.Value = WebsocketConnectionState.Reconnecting;

        _clientWebSocket?.Abort();
        _clientWebSocket?.Dispose();

        await Task.Delay(3000, _dispose.Token);

#pragma warning disable CS4014
        Run(ConnectAsync, _dispose.Token);
#pragma warning restore CS4014
    }
    
    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

#if NET7_0_OR_GREATER
        await _dispose.CancelAsync();
#else
        _dispose.Cancel();
#endif
        await OnDispose.Raise();
        _clientWebSocket?.Dispose();
    }

    public Task Run(Func<Task?> function, CancellationToken cancellationToken = default,
        [CallerFilePath] string file = "",
        [CallerMemberName] string member = "", [CallerLineNumber] int line = -1)
        => Task.Run(function, cancellationToken).ContinueWith(
            t =>
            {
                if (!t.IsFaulted) return;
                var index = file.LastIndexOf('\\');
                if (index == -1) index = file.LastIndexOf('/');
                _logger?.LogError(t.Exception,
                    "Error during task execution. {File}::{Member}:{Line} - Stack: {Stack}",
                    file.Substring(index + 1, file.Length - index - 1), member, line, t.Exception?.StackTrace);
            }, TaskContinuationOptions.OnlyOnFaulted);

    public Task Run(Task? function, CancellationToken cancellationToken = default, [CallerFilePath] string file = "",
        [CallerMemberName] string member = "", [CallerLineNumber] int line = -1)
        => Task.Run(() => function, cancellationToken).ContinueWith(
            t =>
            {
                if (!t.IsFaulted) return;
                var index = file.LastIndexOf('\\');
                if (index == -1) index = file.LastIndexOf('/');
                _logger?.LogError(t.Exception,
                    "Error during task execution. {File}::{Member}:{Line} - Stack: {Stack}",
                    file.Substring(index + 1, file.Length - index - 1), member, line, t.Exception?.StackTrace);
            }, TaskContinuationOptions.OnlyOnFaulted);
}