using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ITouch
{
    /// <summary>
    /// ITouch WebSocket client for iOS automation without jailbreak.
    /// </summary>
    public class Client : IDisposable
    {
        private readonly Dictionary<string, TaskCompletionSource<object>> _wsEvents = new Dictionary<string, TaskCompletionSource<object>>();
        private readonly EventEmitter _eventEmitter = new EventEmitter();
        private readonly ClientOptions _options;
        private ClientWebSocket _wsClient;
        private CancellationTokenSource _cancellationTokenSource;
        private Timer _reconnectTimer;
        private bool _closed;
        private bool _connected;
        private int _reconnectAttempts;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Create a new ITouch client instance.
        /// </summary>
        /// <param name="options">Client options. If null, default options will be used.</param>
        public Client(ClientOptions options = null)
        {
            _options = options != null ? options : new ClientOptions();
        }

        /// <summary>
        /// Connect to the WebSocket server.
        /// </summary>
        public async Task Connect()
        {
            if (_closed)
                throw new InvalidOperationException("[iTouch] client is destroyed");

            await _lock.WaitAsync();
            try
            {
                if (_wsClient != null && _connected)
                    return;
            }
            finally
            {
                _lock.Release();
            }

            await ConnectInternalAsync();
        }

        private async Task ConnectInternalAsync()
        {
            if (_closed)
                return;

            await _lock.WaitAsync();
            try
            {
                if (_wsClient != null && _connected)
                    return;

                if (_wsClient != null)
                {
                    try
                    {
                        await _wsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", CancellationToken.None);
                    }
                    catch { }
                    _wsClient.Dispose();
                    _wsClient = null;
                }
            }
            finally
            {
                _lock.Release();
            }

            try
            {
                var uri = new Uri($"ws://{_options.Host}:{_options.Port}");
                _wsClient = new ClientWebSocket();
                _cancellationTokenSource = new CancellationTokenSource();

                await _wsClient.ConnectAsync(uri, _cancellationTokenSource.Token);
                _connected = true;
                _reconnectAttempts = 0;

                _ = Task.Run(async () => await ReceiveLoopAsync(), _cancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[iTouch] Connection failed: {ex.Message}");
                _connected = false;
                if (!_closed)
                {
                    Reconnect();
                }
            }
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[1024 * 4];
            var messageBuffer = new List<byte>();

            while (_wsClient != null && _wsClient.State == WebSocketState.Open && (_cancellationTokenSource == null || !_cancellationTokenSource.Token.IsCancellationRequested))
            {
                try
                {
                    var token = _cancellationTokenSource != null ? _cancellationTokenSource.Token : CancellationToken.None;
                    var result = await _wsClient.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await HandleCloseAsync();
                        break;
                    }

                    messageBuffer.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));

                    if (result.EndOfMessage)
                    {
                        await ProcessMessageAsync(messageBuffer.ToArray());
                        messageBuffer.Clear();
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[iTouch] Receive error: {ex.Message}");
                    break;
                }
            }

            await HandleCloseAsync();
        }

        private async Task ProcessMessageAsync(byte[] data)
        {
            try
            {
                JsonElement payload;
                byte[] binaryData = null;

                var firstByte = data.Length > 0 ? data[0] : 0;
                var isJson = firstByte == 0x7B || firstByte == 0x5B; // { or [

                if (isJson)
                {
                    var jsonString = Encoding.UTF8.GetString(data);
                    payload = JsonSerializer.Deserialize<JsonElement>(jsonString);
                }
                else
                {
                    var metaLengthStr = Encoding.UTF8.GetString(data, 0, 6).Trim();
                    if (int.TryParse(metaLengthStr, out var metaLengthInt) && data.Length >= 6 + metaLengthInt)
                    {
                        var metaJson = Encoding.UTF8.GetString(data, 6, metaLengthInt);
                        payload = JsonSerializer.Deserialize<JsonElement>(metaJson);
                        if (data.Length > 6 + metaLengthInt)
                        {
                            binaryData = new byte[data.Length - 6 - metaLengthInt];
                            Array.Copy(data, 6 + metaLengthInt, binaryData, 0, binaryData.Length);
                        }
                    }
                    else
                    {
                        Console.Error.WriteLine("[iTouch] Failed to parse binary message metadata");
                        return;
                    }
                }

                if (payload.ValueKind == JsonValueKind.Null)
                    return;

                // 完全按照 Node.js 逻辑实现
                // const _eventId = _payload.evtid
                // const _event = wsevents[ _eventId ]
                string eventId = null;
                TaskCompletionSource<object> tcs = null;
                
                if (payload.TryGetProperty("evtid", out var evtIdElement))
                {
                    eventId = evtIdElement.GetString();
                    if (eventId != null && _wsEvents.ContainsKey(eventId))
                    {
                        tcs = _wsEvents[eventId];
                    }
                }

                // if( _event ){
                if (tcs != null)
                {
                    // if( _payload.type === 'error' ){
                    if (payload.TryGetProperty("type", out var typeElement) && typeElement.GetString() == "error")
                    {
                        // _event.reject(new Error(_payload.error))
                        var errorMsg = "Unknown error";
                        if (payload.TryGetProperty("error", out var errorElement))
                        {
                            errorMsg = errorElement.GetString();
                        }
                        tcs.TrySetException(new Exception(errorMsg));
                    }
                    else
                    {
                        // _event.resolve( _payload.data )
                        object result = null;
                        if (binaryData != null)
                        {
                            result = binaryData;
                        }
                        else if (payload.TryGetProperty("data", out var dataElement))
                        {
                            result = JsonSerializer.Deserialize<object>(dataElement.GetRawText());
                        }
                        tcs.TrySetResult(result);
                    }
                    // delete wsevents[ _eventId ]
                    _wsEvents.Remove(eventId);
                }
                // }else if( _payload.event ){
                else if (payload.TryGetProperty("event", out var eventElement))
                {
                    // eventEmitter.emit(_payload.event, _payload.data)
                    var eventName = eventElement.GetString();
                    if (eventName != null)
                    {
                        object eventData = null;
                        if (payload.TryGetProperty("data", out var dataElement))
                        {
                            eventData = JsonSerializer.Deserialize<object>(dataElement.GetRawText());
                        }
                        _eventEmitter.Emit(eventName, eventData);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[iTouch] receive message error: {ex.Message}");
            }
        }

        private async Task HandleCloseAsync()
        {
            var wasConnected = _connected;
            _connected = false;

            await _lock.WaitAsync();
            try
            {
                if (_wsClient != null)
                {
                    try
                    {
                        if (_wsClient.State == WebSocketState.Open || _wsClient.State == WebSocketState.Connecting)
                        {
                            await _wsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        }
                    }
                    catch { }
                    _wsClient.Dispose();
                    _wsClient = null;
                }
                if (_cancellationTokenSource != null)
                {
                    _cancellationTokenSource.Cancel();
                    _cancellationTokenSource.Dispose();
                    _cancellationTokenSource = null;
                }
            }
            finally
            {
                _lock.Release();
            }

            if (!_closed)
            {
                if (wasConnected)
                {
                    Console.WriteLine($"[iTouch] Connection closed, will reconnect in {_options.ReconnectDelay} seconds...");
                }
                Reconnect();
            }
        }

        private void Reconnect()
        {
            if (_closed || !_options.AutoReconnect)
                return;

            if (_options.MaxReconnectAttempts > 0 && _reconnectAttempts >= _options.MaxReconnectAttempts)
            {
                Console.Error.WriteLine($"[iTouch] Max reconnect attempts ({_options.MaxReconnectAttempts}) reached. Stopping reconnection.");
                return;
            }

            if (_reconnectTimer != null)
            {
                _reconnectTimer.Dispose();
            }
            _reconnectTimer = new Timer(async _ =>
            {
                _reconnectAttempts++;
                var attemptsInfo = _options.MaxReconnectAttempts > 0 
                    ? $"({_reconnectAttempts}/{_options.MaxReconnectAttempts})" 
                    : $"({_reconnectAttempts})";
                Console.WriteLine($"[iTouch] Attempting to reconnect to ws://{_options.Host}:{_options.Port}... {attemptsInfo}");
                await ConnectInternalAsync();
            }, null, TimeSpan.FromSeconds(_options.ReconnectDelay), Timeout.InfiniteTimeSpan);
        }

        /// <summary>
        /// Invoke an API method.
        /// </summary>
        /// <param name="type">API type</param>
        /// <param name="params">Request parameters</param>
        /// <param name="timeout">Timeout in seconds. Default: 18</param>
        /// <returns>Response data</returns>
        public async Task<object> Invoke(string type, Dictionary<string, object> @params = null, int timeout = 18)
        {
            if (_closed)
                throw new InvalidOperationException("[iTouch] client is destroyed");

            if (_wsClient == null || _wsClient.State != WebSocketState.Open)
                throw new InvalidOperationException("[iTouch] websocket not connected");

            if (@params == null)
            {
                @params = new Dictionary<string, object>();
            }

            var eventId = Guid.NewGuid().ToString("N").Substring(0, 10);
            var payload = new Dictionary<string, object>
            {
                ["type"] = type,
                ["evtid"] = eventId,
                ["timeout"] = timeout
            };

            foreach (var kvp in @params)
            {
                payload[kvp.Key] = kvp.Value;
            }

            var tcs = new TaskCompletionSource<object>();
            _wsEvents[eventId] = tcs;

            try
            {
                var json = JsonSerializer.Serialize(payload);
                var bytes = Encoding.UTF8.GetBytes(json);
                var sendToken = _cancellationTokenSource != null ? _cancellationTokenSource.Token : CancellationToken.None;
                await _wsClient.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, sendToken);

                if (timeout > 0)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(timeout));
                        if (_wsEvents.TryGetValue(eventId, out var timeoutTcs) && !timeoutTcs.Task.IsCompleted)
                        {
                            timeoutTcs.TrySetException(new TimeoutException($"[iTouch] API: {eventId} => {type} Invoke Timeout !"));
                            _wsEvents.Remove(eventId);
                        }
                    });
                }

                return await tcs.Task;
            }
            catch (Exception ex)
            {
                _wsEvents.Remove(eventId);
                throw new Exception($"[iTouch] Send failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Register an event listener.
        /// </summary>
        /// <param name="eventName">Event name</param>
        /// <param name="callback">Callback function</param>
        public void On(string eventName, Action<object> callback)
        {
            _eventEmitter.On(eventName, callback);
        }

        /// <summary>
        /// Remove an event listener.
        /// </summary>
        /// <param name="eventName">Event name</param>
        /// <param name="callback">Callback function to remove. If null, removes all listeners for the event.</param>
        public void Off(string eventName, Action<object> callback = null)
        {
            _eventEmitter.Off(eventName, callback);
        }

        /// <summary>
        /// Destroy the client, disconnect and clean up all resources.
        /// </summary>
        public void Dispose()
        {
            _closed = true;

            if (_reconnectTimer != null)
            {
                _reconnectTimer.Dispose();
            }
            _reconnectTimer = null;

            _lock.Wait();
            try
            {
                if (_wsClient != null)
                {
                    try
                    {
                        if (_wsClient.State == WebSocketState.Open || _wsClient.State == WebSocketState.Connecting)
                        {
                            _wsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None).Wait();
                        }
                    }
                    catch { }
                    _wsClient.Dispose();
                    _wsClient = null;
                }
                if (_cancellationTokenSource != null)
                {
                    _cancellationTokenSource.Cancel();
                    _cancellationTokenSource.Dispose();
                    _cancellationTokenSource = null;
                }
            }
            finally
            {
                _lock.Release();
            }

            _eventEmitter.ClearAll();

            foreach (var kvp in _wsEvents)
            {
                kvp.Value.TrySetException(new Exception("[iTouch] itouch client is destroyed"));
            }
            _wsEvents.Clear();

            _lock.Dispose();
        }
    }
}

