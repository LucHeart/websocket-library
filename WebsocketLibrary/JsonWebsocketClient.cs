using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using LucHeart.WebsocketLibrary.Reconnection;
using LucHeart.WebsocketLibrary.Updatables;
using LucHeart.WebsocketLibrary.Utils;
using Microsoft.Extensions.Logging;
using OneOf.Types;
using OpenShock.MinimalEvents;

namespace LucHeart.WebsocketLibrary;

public sealed class JsonWebsocketClient<TRec, TSend> : IAsyncDisposable
{
    private readonly JsonSerializerOptions _jsonSerializerOptions = JsonSerializerOptions.Default;

    private bool _disposed;
    private readonly WebsocketConnectHook _connectHook;

    private readonly ILogger? _logger;
    private ClientWebSocket? _clientWebSocket;

    private readonly CancellationTokenSource _dispose;
    private CancellationTokenSource? _currentConnectionToken;
    private readonly IDictionary<string, string> _headers = new Dictionary<string, string>();
    private IReconnectPolicy _reconnectPolicy;
    private ReconnectionContext _reconnectionContext = new();

    private Channel<TSend> _channel = Channel.CreateUnbounded<TSend>();

    public JsonWebsocketClient(WebsocketConnectHook connectHook, WebsocketClientOptions? options = null) : this(options)
    {
        _connectHook = connectHook;
    }

    public JsonWebsocketClient(Uri uri, WebsocketClientOptions? options = null) : this(options)
    {
        _connectHook = () => Task.FromResult(OneOf.OneOf<WebsocketConnectOptions, Error>.FromT0(
            new WebsocketConnectOptions
            {
                Uri = uri
            }));
    }

#pragma warning disable CS8618 // Connect hook is set by the two public constructors, so it is never null.
    private JsonWebsocketClient(WebsocketClientOptions? options = null)
#pragma warning restore CS8618
    {
        _dispose = new CancellationTokenSource();

        _logger = options?.Logger;

        _reconnectPolicy = options?.ReconnectPolicy ?? new DefaultReconnectPolicy();

        if (options is null) return;
        _headers = options.Headers;

        if (options.JsonSerializerOptions is not null)
            _jsonSerializerOptions = options.JsonSerializerOptions;
    }

    public ValueTask QueueMessage(TSend data) =>
        _channel.Writer.WriteAsync(data, _dispose.Token);

    public IAsyncUpdatable<WebsocketConnectionState> State => _state;

    private readonly AsyncUpdatableVariable<WebsocketConnectionState> _state =
        new(WebsocketConnectionState.NotStarted);

    public IAsyncMinimalEventObservable<TRec> OnMessage => _onMessage;
    private readonly AsyncMinimalEvent<TRec> _onMessage = new();

    private async Task MessageLoop(Channel<TSend> channel, ClientWebSocket websocket, CancellationToken token)
    {
        try
        {
            await foreach (var msg in channel.Reader.ReadAllAsync(token))
                await JsonWebSocketUtils.SendFullMessage(msg, websocket, token, _jsonSerializerOptions);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Error in message loop");
        }
    }

    private bool _isStarted;

    /// <summary>
    /// Start the websocket.
    /// </summary>
    /// <returns>False if it has been started before, or disposed</returns>
    public bool Start()
    {
        if (_disposed)
        {
            _logger?.LogWarning("StartAsync called after disposed, ignoring");
            return false;
        }

#if NET7_0_OR_GREATER
        if (Interlocked.CompareExchange(ref _isStarted, true, false))
        {
            _logger?.LogWarning("StartAsync called while already started, ignoring");
            return false;
        }
#else
        if(_isStarted)
        {
            _logger?.LogWarning("StartAsync called while already started, ignoring");
            return false;
        }
        
        _isStarted = true;
#endif


        Run(ReconnectionLoop);

        return true;
    }

    private async Task ReconnectionLoop()
    {
        while (!_dispose.IsCancellationRequested)
        {
            try
            {
                await WebsocketLifetime();
            }
            catch (Exception e)
            {
                _logger?.LogError(e, "Error in websocket lifetime, reconnecting...");
            }

            if (_dispose.IsCancellationRequested)
            {
                _logger?.LogWarning("Dispose requested, not reconnecting");
                _state.Value = WebsocketConnectionState.Disconnected;
                return;
            }

            _state.Value = WebsocketConnectionState.WaitingForReconnect;
            
            _reconnectionContext.Attempt += 1;
            var waitTime = _reconnectPolicy.NextReconnectionDelay(_reconnectionContext);
            _logger?.LogInformation("Waiting for {WaitTime} seconds before reconnecting, attempt {Attempt}", waitTime, _reconnectionContext.Attempt);
            await Task.Delay(waitTime, _dispose.Token);
        }
    }

    private async Task WebsocketLifetime()
    {
        _state.Value = WebsocketConnectionState.Connecting;

        _currentConnectionToken = new CancellationTokenSource();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_dispose.Token, _currentConnectionToken.Token);
        var cancellationToken = linked.Token;

        _channel = Channel.CreateUnbounded<TSend>();
        ClientWebSocket currentClientWebSocket;
        _clientWebSocket = currentClientWebSocket = new ClientWebSocket();

        foreach (var pair in _headers)
        {
            currentClientWebSocket.Options.SetRequestHeader(pair.Key, pair.Value);
        }

        if (await ConnectWebsocket(currentClientWebSocket, cancellationToken))
        {
#pragma warning disable CS4014
            Run(MessageLoop(_channel, currentClientWebSocket, cancellationToken), cancellationToken);
#pragma warning restore CS4014

            _reconnectionContext.Attempt = 0;
            _state.Value = WebsocketConnectionState.Connected;

            await NewReceiveLoop(currentClientWebSocket, cancellationToken);
        }

        // Only send close if the socket is still open, this allows us to close the websocket from inside the logic
        // We send close if the client sent a close message though
        if (currentClientWebSocket is { State: WebSocketState.Open or WebSocketState.CloseReceived })
        {
            try
            {
                await currentClientWebSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Normal closure",
                    _dispose.Token);
            }
            catch (TaskCanceledException) when (_dispose.IsCancellationRequested)
            {
                // Ignore, this happens when the websocket is disposed
            }
        }

        currentClientWebSocket.Abort();
        currentClientWebSocket.Dispose();
    }

    private async Task<bool> ConnectWebsocket(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        try
        {
            _logger?.LogDebug("Running connect hook");
            var connectHook = await _connectHook.Invoke();
            if (connectHook.IsT1)
            {
                _logger?.LogWarning("Websocket connection stopped, not connecting");
                return false;
            }

            var uri = connectHook.AsT0.Uri;
            _logger?.LogDebug("Connecting to websocket at {Uri}", uri);
            await webSocket.ConnectAsync(uri, cancellationToken);

            _logger?.LogInformation("Connected to websocket");
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Error while connecting to websocket");
            return false;
        }

        return true;
    }

    private async Task NewReceiveLoop(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (webSocket.State is WebSocketState.CloseReceived or WebSocketState.CloseSent
                    or WebSocketState.Closed)
                {
                    // Client or we sent close message or both, we will close the connection after this
                    return;
                }

                if (webSocket.State != WebSocketState.Open)
                {
                    _logger?.LogWarning("WebSocket is not open [{State}], aborting", webSocket.State);
                    webSocket.Abort();
                    return;
                }

                if (!await HandleReceive(webSocket, cancellationToken))
                {
                    // HandleReceive returned false, we will close the connection after this
                    _logger?.LogDebug("HandleReceive returned false, closing connection");
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                // When we dont receive a close message from the client, we will get this exception
                webSocket.Abort();
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Exception while processing websocket request");
                webSocket.Abort();
                return;
            }
        }
    }

    private async Task<bool> HandleReceive(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var message =
            await JsonWebSocketUtils.ReceiveFullMessageAsyncNonAlloc<TRec>(webSocket, cancellationToken);

        var continueLoop = await message.Match(async request =>
            {
                if (request is null)
                {
                    _logger?.LogWarning("Received null data from client");
                    await ForceClose(webSocket, WebSocketCloseStatus.InvalidPayloadData, "Null json message received");
                    return false;
                }


#pragma warning disable CS4014
                Run(async () => await _onMessage.InvokeAsyncParallel(request));
#pragma warning restore CS4014

                return true;
            },
            async failed =>
            {
                _logger?.LogWarning(failed.Exception, "Deserialization failed for websocket message");
                await ForceClose(webSocket, WebSocketCloseStatus.InvalidPayloadData, "Invalid json message received");
                return false;
            }, closure =>
            {
                _logger?.LogTrace("Client sent closure");
                return Task.FromResult(false);
            });

        return continueLoop;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_clientWebSocket is not null)
        {
            try
            {
                await ForceClose(_clientWebSocket, WebSocketCloseStatus.NormalClosure, "Normal close");
            }
            catch (Exception e)
            {
                _logger?.LogError(e, "Error closing during dispose");
            }

            _clientWebSocket.Abort();
            _clientWebSocket.Dispose();
        }

#if NET7_0_OR_GREATER
        await _dispose.CancelAsync();
#else
        _dispose.Cancel();
#endif
    }

    private async Task ForceClose(ClientWebSocket webSocket, WebSocketCloseStatus closeStatus,
        string? statusDescription)
    {
#if NET7_0_OR_GREATER
        if (_currentConnectionToken is not null) await _currentConnectionToken.CancelAsync();
#else
        _currentConnectionToken?.Cancel();
#endif

        if (webSocket is { State: WebSocketState.CloseReceived or WebSocketState.Open })
        {
            await webSocket.CloseOutputAsync(closeStatus, statusDescription, _dispose.Token);
        }
    }

    private Task Run(Func<Task?> function, CancellationToken cancellationToken = default,
        [CallerFilePath] string file = "",
        [CallerMemberName] string member = "", [CallerLineNumber] int line = -1)
    {
        var task = Task.Run(function, cancellationToken);
        task.ContinueWith(
            t =>
            {
                if (!t.IsFaulted) return;
                var index = file.LastIndexOf('\\');
                if (index == -1) index = file.LastIndexOf('/');
                _logger?.LogError(t.Exception,
                    "Error during task execution. {File}::{Member}:{Line} - Stack: {Stack}",
                    file.Substring(index + 1, file.Length - index - 1), member, line, t.Exception?.StackTrace);
            }, TaskContinuationOptions.OnlyOnFaulted);
        return task;
    }

    private Task Run(Task? function, CancellationToken cancellationToken = default, [CallerFilePath] string file = "",
        [CallerMemberName] string member = "", [CallerLineNumber] int line = -1)
    {
        var task = Task.Run(() => function, cancellationToken);
        task.ContinueWith(
            t =>
            {
                if (!t.IsFaulted) return;
                var index = file.LastIndexOf('\\');
                if (index == -1) index = file.LastIndexOf('/');
                _logger?.LogError(t.Exception,
                    "Error during task execution. {File}::{Member}:{Line} - Stack: {Stack}",
                    file.Substring(index + 1, file.Length - index - 1), member, line, t.Exception?.StackTrace);
            }, TaskContinuationOptions.OnlyOnFaulted);
        return task;
    }
}