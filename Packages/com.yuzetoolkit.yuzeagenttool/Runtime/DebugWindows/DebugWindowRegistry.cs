#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace YuzeToolkit.Agent
{
    internal static class DebugWindowRegistry
    {
        private static readonly List<DebugWindowRegistration> Registrations = new();
        private static int _revision;

        public static IReadOnlyList<DebugWindowRegistration> RegisteredWindows => Registrations;
        public static int Revision => _revision;

        public static IDisposable RegisterWindow(Action<DebugWindowBuilder> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            var builder = new DebugWindowBuilder();
            configure(builder);
            var registration = DebugWindowRegistration.Create(builder);
            Registrations.Add(registration);
            _revision++;
            return new Handle(registration);
        }

        private static void Unregister(DebugWindowRegistration registration)
        {
            if (!Registrations.Remove(registration)) return;
            _revision++;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRuntimeState()
        {
            Registrations.Clear();
            _revision++;
        }

        private sealed class Handle : IDisposable
        {
            private DebugWindowRegistration? _registration;

            public Handle(DebugWindowRegistration registration) => _registration = registration;

            public void Dispose()
            {
                if (_registration == null) return;
                Unregister(_registration);
                _registration = null;
            }
        }
    }
}
