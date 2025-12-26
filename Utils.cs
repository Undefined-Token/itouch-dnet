namespace ITouch
{
    /// <summary>
    /// Utility class containing default configuration values.
    /// </summary>
    public static class Utils
    {
        /// <summary>
        /// Default WebSocket server host.
        /// </summary>
        public const string WebSocketHost = "127.0.0.1";

        /// <summary>
        /// Default WebSocket server port.
        /// </summary>
        public const int WebSocketPort = 23188;

        /// <summary>
        /// Default reconnection delay in seconds.
        /// </summary>
        public const int ReconnectDelay = 3;

        /// <summary>
        /// Maximum number of reconnection attempts. 0 means unlimited.
        /// </summary>
        public const int MaxReconnectAttempts = 8;
    }
}

