#nullable enable
using System;
using System.Collections.Generic;

namespace YuzeToolkit
{
    public sealed class EvalSessionRegistry : IDisposable
    {
        private readonly Dictionary<string, EvalSession> _sessions = new(StringComparer.Ordinal);
        private readonly object _syncRoot = new();

        public int Count
        {
            get
            {
                lock (_syncRoot) return _sessions.Count;
            }
        }

        public EvalSession Create(string protocolVersion, string clientName, int maxSessions)
        {
            lock (_syncRoot)
            {
                if (_sessions.Count >= maxSessions)
                    throw new InvalidOperationException($"Eval session limit reached ({maxSessions}).");

                var session = new EvalSession(Guid.NewGuid().ToString("N"), protocolVersion, clientName);
                _sessions.Add(session.Id, session);
                return session;
            }
        }

        public bool TryGet(string sessionId, out EvalSession session)
        {
            lock (_syncRoot)
            {
                if (_sessions.TryGetValue(sessionId, out session!))
                {
                    session.Touch();
                    return true;
                }
            }

            session = null!;
            return false;
        }

        public bool Remove(string sessionId)
        {
            EvalSession? session = null;
            lock (_syncRoot)
            {
                if (_sessions.TryGetValue(sessionId, out session))
                    _sessions.Remove(sessionId);
            }

            session?.Dispose();
            return session != null;
        }

        public bool TryRemoveIdle(string sessionId, out bool isRunning)
        {
            isRunning = false;
            EvalSession? session = null;
            lock (_syncRoot)
            {
                if (!_sessions.TryGetValue(sessionId, out session))
                    return false;

                if (session.IsEvalRunning)
                {
                    isRunning = true;
                    return false;
                }

                _sessions.Remove(sessionId);
            }

            session.Dispose();
            return true;
        }

        public List<EvalSessionSnapshot> GetSnapshots()
        {
            lock (_syncRoot)
            {
                var snapshots = new List<EvalSessionSnapshot>(_sessions.Count);
                foreach (var session in _sessions.Values)
                    snapshots.Add(session.ToSnapshot());
                return snapshots;
            }
        }

        public void RemoveIdle(TimeSpan idleTime)
        {
            var now = DateTime.UtcNow;
            var removed = new List<EvalSession>();
            lock (_syncRoot)
            {
                foreach (var pair in _sessions)
                {
                    if (!pair.Value.IsEvalRunning && now - pair.Value.LastSeenUtc >= idleTime)
                        removed.Add(pair.Value);
                }

                foreach (var session in removed)
                    _sessions.Remove(session.Id);
            }

            foreach (var session in removed)
                session.Dispose();
        }

        public void Dispose()
        {
            List<EvalSession> sessions;
            lock (_syncRoot)
            {
                sessions = new List<EvalSession>(_sessions.Values);
                _sessions.Clear();
            }

            foreach (var session in sessions)
                session.Dispose();
        }
    }
}
