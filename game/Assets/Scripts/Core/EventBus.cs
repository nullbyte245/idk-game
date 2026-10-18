using System;
using System.Collections.Generic;

namespace Game.Core
{
    /// <summary>Marker for strongly typed events published through <see cref="EventBus"/>.</summary>
    public interface IGameEvent { }

    /// <summary>
    /// Lightweight typed pub/sub. Handlers are snapshotted before invoking so a handler may
    /// unsubscribe (or subscribe) without invalidating the iteration.
    /// </summary>
    public sealed class EventBus : IService
    {
        private readonly Dictionary<Type, List<object>> _handlers = new Dictionary<Type, List<object>>();

        public void OnRegister(IServiceRegistry registry) { }
        public void OnDispose() => _handlers.Clear();

        public IDisposable Subscribe<T>(Action<T> handler) where T : IGameEvent
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            var type = typeof(T);
            if (!_handlers.TryGetValue(type, out var list))
            {
                list = new List<object>();
                _handlers.Add(type, list);
            }
            list.Add(handler);

            return new Subscription(this, type, handler);
        }

        public void Publish<T>(in T args) where T : IGameEvent
        {
            if (!_handlers.TryGetValue(typeof(T), out var list) || list.Count == 0) return;

            var snapshot = list.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i] is Action<T> action) action.Invoke(args);
            }
        }

        public int HandlerCount<T>() where T : IGameEvent
            => _handlers.TryGetValue(typeof(T), out var list) ? list.Count : 0;

        private void Unsubscribe(Type type, object handler)
        {
            if (!_handlers.TryGetValue(type, out var list)) return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(list[i], handler))
                {
                    list.RemoveAt(i);
                    break;
                }
            }
            if (list.Count == 0) _handlers.Remove(type);
        }

        private sealed class Subscription : IDisposable
        {
            private EventBus _bus;
            private readonly Type _type;
            private readonly object _handler;

            public Subscription(EventBus bus, Type type, object handler)
            {
                _bus = bus;
                _type = type;
                _handler = handler;
            }

            public void Dispose()
            {
                if (_bus == null) return;
                _bus.Unsubscribe(_type, _handler);
                _bus = null;
            }
        }
    }
}
