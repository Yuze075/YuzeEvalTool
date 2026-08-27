#nullable enable
using System;
using YuzeToolkit.Eval;

namespace YuzeToolkit.Agent
{
    /// <summary>
    /// Public registration entry point for Debug Panel pages. This API owns visual pages only;
    /// Eval Tools are registered independently through EvalToolRegistry.
    /// </summary>
    public static class DebugWindowModule
    {
        internal static System.Collections.Generic.IReadOnlyList<DebugWindowRegistration> RegisteredWindows =>
            DebugWindowRegistry.RegisteredWindows;

        public static IDisposable RegisterWindow(Action<DebugWindowBuilder> configure) =>
            DebugWindowRegistry.RegisterWindow(configure);
    }
}
