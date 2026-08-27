#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace YuzeToolkit.Agent
{
    internal sealed class RuntimeLogStore
    {
        private static readonly RuntimeLogStore SharedStore = new();

        private readonly Queue<DebugLogEntry> _pendingEntries = new();
        private readonly object _pendingSyncRoot = new();
        private readonly List<DebugLogEntry> _entries = new();
        private int _maxEntries = 500;
        private int _droppedCount;
        private bool _subscribed;

        private RuntimeLogStore()
        {
        }

        public static RuntimeLogStore Shared
        {
            get
            {
                EnsureSubscribed();
                return SharedStore;
            }
        }

        public int MaxEntries
        {
            get => Volatile.Read(ref _maxEntries);
            set => Volatile.Write(ref _maxEntries, Math.Max(1, value));
        }

        public IReadOnlyList<DebugLogEntry> Entries => _entries;

        public static void EnsureSubscribed() => SharedStore.Subscribe();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitializeRuntimeCapture() => EnsureSubscribed();

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void InitializeEditorCapture()
        {
            EnsureSubscribed();
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= ShutdownEditorCapture;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += ShutdownEditorCapture;
            UnityEditor.EditorApplication.quitting -= ShutdownEditorCapture;
            UnityEditor.EditorApplication.quitting += ShutdownEditorCapture;
        }

        private static void ShutdownEditorCapture()
        {
            if (!SharedStore._subscribed) return;
            SharedStore._subscribed = false;
            Application.logMessageReceivedThreaded -= SharedStore.OnLogMessageReceived;
        }
#endif

        private void Subscribe()
        {
            if (_subscribed) return;
            _subscribed = true;
            Application.logMessageReceivedThreaded += OnLogMessageReceived;
        }

        public void AddInternal(
            string message,
            string stackTrace,
            DebugLogKind kind,
            LogType type = LogType.Log)
        {
            Enqueue(new DebugLogEntry(DateTime.Now, message, stackTrace, type, kind));
        }

        public bool Pump()
        {
            var changed = false;
            const int pumpBudget = 128;
            var dropped = 0;
            lock (_pendingSyncRoot)
            {
                for (var index = 0; index < pumpBudget && _pendingEntries.Count > 0; index++)
                {
                    _entries.Add(_pendingEntries.Dequeue());
                    changed = true;
                }

                dropped = _droppedCount;
                _droppedCount = 0;
            }

            if (dropped > 0)
            {
                _entries.Add(new DebugLogEntry(
                    DateTime.Now,
                    $"Yuze Agent Tool Log dropped {dropped} entries while its bounded capture queue was full.",
                    string.Empty,
                    LogType.Warning,
                    DebugLogKind.Internal));
                changed = true;
            }

            var overflow = _entries.Count - MaxEntries;
            if (overflow > 0)
            {
                _entries.RemoveRange(0, overflow);
                changed = true;
            }

            return changed;
        }

        public void Clear()
        {
            _entries.Clear();
            lock (_pendingSyncRoot)
            {
                _pendingEntries.Clear();
                _droppedCount = 0;
            }
        }

        private void OnLogMessageReceived(string message, string stackTrace, LogType type)
        {
            Enqueue(new DebugLogEntry(
                DateTime.Now,
                message ?? string.Empty,
                stackTrace ?? string.Empty,
                type,
                DebugLogKind.Unity));
        }

        private void Enqueue(DebugLogEntry entry)
        {
            var maxEntries = MaxEntries;
            var capacity = maxEntries > int.MaxValue / 2 ? int.MaxValue : Math.Max(64, maxEntries * 2);
            lock (_pendingSyncRoot)
            {
                _pendingEntries.Enqueue(entry);
                while (_pendingEntries.Count > capacity)
                {
                    _pendingEntries.Dequeue();
                    _droppedCount++;
                }
            }
        }
    }
}
