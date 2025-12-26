using System;
using System.Collections.Generic;

namespace ITouch
{
    /// <summary>
    /// Simple event emitter implementation for handling events.
    /// </summary>
    public class EventEmitter
    {
        private readonly Dictionary<string, List<Action<object>>> _eventListeners = new Dictionary<string, List<Action<object>>>();

        /// <summary>
        /// Register an event listener.
        /// </summary>
        /// <param name="eventName">Event name</param>
        /// <param name="callback">Callback function</param>
        public void On(string eventName, Action<object> callback)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            if (!_eventListeners.ContainsKey(eventName))
            {
                _eventListeners[eventName] = new List<Action<object>>();
            }

            _eventListeners[eventName].Add(callback);
        }

        /// <summary>
        /// Remove an event listener.
        /// </summary>
        /// <param name="eventName">Event name</param>
        /// <param name="callback">Callback function to remove. If null, removes all listeners for the event.</param>
        public void Off(string eventName, Action<object> callback = null)
        {
            if (!_eventListeners.ContainsKey(eventName))
                return;

            if (callback == null)
            {
                _eventListeners.Remove(eventName);
            }
            else
            {
                _eventListeners[eventName].Remove(callback);
                if (_eventListeners[eventName].Count == 0)
                {
                    _eventListeners.Remove(eventName);
                }
            }
        }

        /// <summary>
        /// Emit an event.
        /// </summary>
        /// <param name="eventName">Event name</param>
        /// <param name="data">Event data</param>
        internal void Emit(string eventName, object data)
        {
            // 参考 Node.js: const listeners = eventListeners[eventName]
            if (!_eventListeners.ContainsKey(eventName))
                return;

            // 参考 Node.js: listeners.forEach(callback => { ... })
            var listeners = _eventListeners[eventName].ToArray();
            foreach (var callback in listeners)
            {
                try
                {
                    callback(data);
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"[iTouch] 事件监听器执行错误: {error}");
                }
            }
        }

        /// <summary>
        /// Clear all event listeners.
        /// </summary>
        public void ClearAll()
        {
            _eventListeners.Clear();
        }
    }
}

