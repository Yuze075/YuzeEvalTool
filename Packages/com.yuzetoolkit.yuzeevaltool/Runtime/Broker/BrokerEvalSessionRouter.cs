#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace YuzeToolkit
{
    internal sealed class BrokerEvalSessionRouter : IDisposable
    {
        private sealed class Entry : IDisposable
        {
            public Entry(string sessionId)
            {
                Session = new EvalSession(sessionId, "broker-2.0", "broker");
                Executor = new EvalExecutor(new EvalOptions
                {
                    DefaultEvalTimeoutSeconds = 30,
                    MaxRequestBodyBytes = 4 * 1024 * 1024
                });
                Cli = new EvalCliCommandService(Executor);
            }

            public EvalSession Session { get; }
            public EvalExecutor Executor { get; }
            public EvalCliCommandService Cli { get; }
            public void Dispose() => Session.Dispose();
        }

        private readonly object _syncRoot = new();
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

        public Task<Dictionary<string, object?>> ExecuteEvalAsync(Dictionary<string, object?> payload,
            CancellationToken cancellationToken)
        {
            var sessionId = RequireString(payload, "sessionId");
            var requestId = RequireString(payload, "requestId");
            var code = RequireString(payload, "code");
            var timeout = EvalData.GetInt(payload, "timeoutSeconds", 30);
            var resetSession = EvalData.GetBool(payload, "resetSession", false);
            var entry = GetOrCreate(sessionId);
            return entry.Executor.ExecuteAsync(entry.Session, requestId, EvalData.Obj(
                ("code", code),
                ("timeout", timeout),
                ("resetSession", resetSession)), cancellationToken);
        }

        public Task<Dictionary<string, object?>> ExecuteCliAsync(Dictionary<string, object?> payload,
            CancellationToken cancellationToken)
        {
            var sessionId = RequireString(payload, "sessionId");
            var requestId = RequireString(payload, "requestId");
            var line = RequireString(payload, "line");
            var entry = GetOrCreate(sessionId);
            return entry.Cli.ExecuteLineAsync(entry.Session, requestId, line, cancellationToken);
        }

        public void Release(string sessionId)
        {
            Entry? entry;
            lock (_syncRoot)
            {
                if (!_entries.TryGetValue(sessionId, out entry)) return;
                _entries.Remove(sessionId);
            }

            entry.Dispose();
        }

        public IReadOnlyList<EvalSessionSnapshot> GetSnapshots(string sessionPrefix)
        {
            lock (_syncRoot)
            {
                return _entries
                    .Where(pair => pair.Key.StartsWith(sessionPrefix, StringComparison.Ordinal))
                    .Select(pair => pair.Value.Session.ToSnapshot())
                    .ToList();
            }
        }

        public void Dispose()
        {
            Reset();
        }

        public void Reset()
        {
            Entry[] entries;
            lock (_syncRoot)
            {
                entries = new Entry[_entries.Count];
                _entries.Values.CopyTo(entries, 0);
                _entries.Clear();
            }

            foreach (var entry in entries) entry.Dispose();
        }

        private Entry GetOrCreate(string sessionId)
        {
            lock (_syncRoot)
            {
                if (_entries.TryGetValue(sessionId, out var entry)) return entry;
                entry = new Entry(sessionId);
                _entries.Add(sessionId, entry);
                return entry;
            }
        }

        private static string RequireString(Dictionary<string, object?> payload, string key)
        {
            var value = EvalData.GetString(payload, key);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"Broker payload field '{key}' is required.");
            return value!;
        }
    }
}
