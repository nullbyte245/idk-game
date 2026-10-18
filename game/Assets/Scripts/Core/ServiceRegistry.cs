using System;
using System.Collections.Generic;

namespace Game.Core
{
    public interface IService
    {
        void OnRegister(IServiceRegistry registry);
        void OnDispose();
    }

    public interface IServiceRegistry
    {
        T Get<T>() where T : class, IService;
        bool TryGet<T>(out T service) where T : class, IService;
    }

    /// <summary>
    /// Services are plain objects so they stay unit-testable and survive scene reloads.
    /// Anything that needs a frame callback implements ITickable instead of being a MonoBehaviour.
    /// </summary>
    public sealed class ServiceRegistry : IServiceRegistry, IDisposable
    {
        private readonly Dictionary<Type, IService> _services = new Dictionary<Type, IService>();
        private readonly List<IService> _registrationOrder = new List<IService>();
        private readonly List<ITickable> _tickables = new List<ITickable>();

        public IReadOnlyList<ITickable> Tickables => _tickables;

        public T Register<T>(T service) where T : class, IService
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            var key = typeof(T);
            if (_services.ContainsKey(key))
                throw new InvalidOperationException("Service already registered: " + key.Name);

            _services.Add(key, service);
            _registrationOrder.Add(service);
            service.OnRegister(this);

            if (service is ITickable tickable) _tickables.Add(tickable);
            return service;
        }

        public T Get<T>() where T : class, IService
        {
            if (TryGet<T>(out var service)) return service;
            throw new InvalidOperationException(
                "Service not registered: " + typeof(T).Name + ". Register it in GameRoot before use.");
        }

        public bool TryGet<T>(out T service) where T : class, IService
        {
            if (_services.TryGetValue(typeof(T), out var raw))
            {
                service = (T)raw;
                return true;
            }
            service = null;
            return false;
        }

        public void Dispose()
        {
            for (int i = _registrationOrder.Count - 1; i >= 0; i--)
                _registrationOrder[i].OnDispose();

            _registrationOrder.Clear();
            _tickables.Clear();
            _services.Clear();
        }
    }

    public interface ITickable
    {
        void OnTick(float deltaTime);
    }
}
