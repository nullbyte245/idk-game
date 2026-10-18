using System;

namespace Game.Core
{
    /// <summary>Global handle to the live service registry. Written once by GameRoot.</summary>
    public static class GameServices
    {
        private static ServiceRegistry _registry;

        public static bool IsReady => _registry != null;
        public static ServiceRegistry Registry => _registry;

        public static T Get<T>() where T : class, IService
        {
            if (_registry == null)
                throw new InvalidOperationException(
                    "GameServices is not ready. GameRoot registers services before scene content runs; " +
                    "if you access a service from a scene load callback, guard with IsReady.");
            return _registry.Get<T>();
        }

        public static bool TryGet<T>(out T service) where T : class, IService
        {
            service = null;
            if (_registry == null) return false;
            return _registry.TryGet(out service);
        }

        public static void Attach(ServiceRegistry registry) => _registry = registry;

        public static void Detach(ServiceRegistry registry)
        {
            if (ReferenceEquals(_registry, registry)) _registry = null;
        }
    }
}
