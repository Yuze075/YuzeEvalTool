#nullable enable
using System;
using UnityEngine;

namespace YuzeToolkit
{
    internal enum DebugLogKind
    {
        Unity,
        Internal
    }

    internal sealed class DebugLogEntry
    {
        public DebugLogEntry(DateTime time, string message, string stackTrace, LogType type, DebugLogKind kind)
        {
            Time = time;
            Message = message;
            StackTrace = stackTrace;
            Type = type;
            Kind = kind;
        }

        public DateTime Time { get; }

        public string Message { get; }

        public string StackTrace { get; }

        public LogType Type { get; }

        public DebugLogKind Kind { get; }

        public bool Expanded { get; set; }
    }
}
