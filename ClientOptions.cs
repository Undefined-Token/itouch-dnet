namespace ITouch
{
    /// <summary>
    /// Options for configuring the ITouch client.
    /// </summary>
    public class ClientOptions
    {
        /// <summary>
        /// WebSocket server host. Default: "127.0.0.1"
        /// </summary>
        public string Host { get; set; } = Utils.WebSocketHost;

        /// <summary>
        /// WebSocket server port. Default: 23188
        /// </summary>
        public int Port { get; set; } = Utils.WebSocketPort;

        /// <summary>
        /// Enable automatic reconnection. Default: true
        /// </summary>
        public bool AutoReconnect { get; set; } = true;

        /// <summary>
        /// Reconnection delay in seconds. Default: 3
        /// </summary>
        public int ReconnectDelay { get; set; } = Utils.ReconnectDelay;

        /// <summary>
        /// Maximum number of reconnection attempts. 0 means unlimited. Default: 8
        /// </summary>
        public int MaxReconnectAttempts { get; set; } = Utils.MaxReconnectAttempts;
    }
}

