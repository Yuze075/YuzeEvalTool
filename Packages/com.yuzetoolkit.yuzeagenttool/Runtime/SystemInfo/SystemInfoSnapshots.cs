#nullable enable
using System;
using System.Collections.Generic;

namespace YuzeToolkit.Agent
{
    internal readonly struct SystemInfoRegistration
    {
        public SystemInfoRegistration(string key, Func<string> valueProvider)
        {
            Key = key;
            ValueProvider = valueProvider;
        }

        public string Key { get; }

        public Func<string> ValueProvider { get; }
    }

    internal readonly struct SystemInfoSnapshot
    {
        public SystemInfoSnapshot(IReadOnlyList<SystemInfoLine> lines)
        {
            Lines = lines;
        }

        public IReadOnlyList<SystemInfoLine> Lines { get; }
    }

    internal readonly struct SystemInfoLine
    {
        public SystemInfoLine(string key, string value)
        {
            Key = key;
            Value = value;
        }

        public string Key { get; }

        public string Value { get; }
    }
}
