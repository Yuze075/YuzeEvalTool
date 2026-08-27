#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace YuzeToolkit
{
    public static class UnityLogBuffer
    {
        private struct Entry
        {
            public string Timestamp;
            public string Type;
            public string Message;
            public string StackTrace;
        }

        private static readonly List<Entry> Entries = new();
        private static readonly object SyncRoot = new();
        private static bool _listening;
        private static int _limit = 300;

        public static void Start()
        {
            if (_listening) return;
            Application.logMessageReceived -= OnLogMessageReceived;
            Application.logMessageReceived += OnLogMessageReceived;
            _listening = true;
        }

        public static void Clear()
        {
            lock (SyncRoot)
                Entries.Clear();
        }

        public static List<object?> GetRecent(int count, string logType)
        {
            Start();
            count = Mathf.Clamp(count <= 0 ? 50 : count, 1, _limit);
            var typeFilter = string.IsNullOrWhiteSpace(logType) ? "all" : logType.ToLowerInvariant();
            var result = new List<object?>();

            lock (SyncRoot)
            {
                for (var i = Entries.Count - 1; i >= 0 && result.Count < count; i--)
                {
                    var entry = Entries[i];
                    if (typeFilter != "all" && entry.Type.ToLowerInvariant() != typeFilter)
                        continue;

                    result.Add(EvalData.Obj(
                        ("timestamp", entry.Timestamp),
                        ("type", entry.Type),
                        ("message", entry.Message),
                        ("stackTrace", entry.StackTrace)
                    ));
                }
            }

            result.Reverse();
            return result;
        }

        public static List<object?> GetCompilerLikeMessages(int count)
        {
            Start();
            count = Mathf.Clamp(count <= 0 ? 50 : count, 1, _limit);
            var result = new List<object?>();

            lock (SyncRoot)
            {
                for (var i = Entries.Count - 1; i >= 0 && result.Count < count; i--)
                {
                    var entry = Entries[i];
                    if (!LooksLikeCompilerMessage(entry)) continue;

                    result.Add(EvalData.Obj(
                        ("timestamp", entry.Timestamp),
                        ("type", entry.Type),
                        ("message", entry.Message),
                        ("stackTrace", entry.StackTrace)
                    ));
                }
            }

            return result;
        }

        private static bool LooksLikeCompilerMessage(Entry entry)
        {
            var type = entry.Type.ToLowerInvariant();
            if (type != "error" && type != "warning" && type != "exception" && type != "assert")
                return false;

            var text = (entry.Message + "\n" + entry.StackTrace).ToLowerInvariant();
            return text.Contains(".cs(") || text.Contains("error cs") || text.Contains("warning cs") ||
                   text.Contains(": error") || text.Contains(": warning");
        }

        private static void OnLogMessageReceived(string message, string stackTrace, LogType type)
        {
            lock (SyncRoot)
            {
                Entries.Add(new Entry
                {
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    Type = type.ToString(),
                    Message = message ?? string.Empty,
                    StackTrace = stackTrace ?? string.Empty
                });

                while (Entries.Count > _limit)
                    Entries.RemoveAt(0);
            }
        }
    }
}
