#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace YuzeToolkit.Eval
{
    public sealed class BrokerUnityStatusSnapshot
    {
        public string Phase { get; set; } = "Starting";
        public bool CanEval { get; set; }
        public string BusyReason { get; set; } = string.Empty;
        public long MainThreadTick { get; set; }
        public DateTime MainThreadTickAtUtc { get; set; }
        public bool IsPlaying { get; set; }
        public bool IsPaused { get; set; }
        public bool IsUpdating { get; set; }
        public string CompilationCycleId { get; set; } = string.Empty;
        public int CompilerErrorCount { get; set; }
        public int CompilerWarningCount { get; set; }
        public string LastCompilationStartedAtUtc { get; set; } = string.Empty;
        public string LastCompilationFinishedAtUtc { get; set; } = string.Empty;
        public long VmGeneration { get; set; }

        internal Dictionary<string, object?> ToObject()
        {
            return EvalData.Obj(
                ("phase", Phase),
                ("canEval", CanEval),
                ("busyReason", BusyReason),
                ("mainThreadTick", MainThreadTick),
                ("mainThreadTickAtUtc", MainThreadTickAtUtc.ToString("O")),
                ("isPlaying", IsPlaying),
                ("isPaused", IsPaused),
                ("isUpdating", IsUpdating),
                ("compilationCycleId", CompilationCycleId),
                ("compilerErrorCount", CompilerErrorCount),
                ("compilerWarningCount", CompilerWarningCount),
                ("lastCompilationStartedAtUtc", EmptyToNull(LastCompilationStartedAtUtc)),
                ("lastCompilationFinishedAtUtc", EmptyToNull(LastCompilationFinishedAtUtc)),
                ("vmGeneration", VmGeneration)
            );
        }

        public BrokerUnityStatusSnapshot Clone()
        {
            return new BrokerUnityStatusSnapshot
            {
                Phase = Phase,
                CanEval = CanEval,
                BusyReason = BusyReason,
                MainThreadTick = MainThreadTick,
                MainThreadTickAtUtc = MainThreadTickAtUtc,
                IsPlaying = IsPlaying,
                IsPaused = IsPaused,
                IsUpdating = IsUpdating,
                CompilationCycleId = CompilationCycleId,
                CompilerErrorCount = CompilerErrorCount,
                CompilerWarningCount = CompilerWarningCount,
                LastCompilationStartedAtUtc = LastCompilationStartedAtUtc,
                LastCompilationFinishedAtUtc = LastCompilationFinishedAtUtc,
                VmGeneration = VmGeneration
            };
        }

        internal static BrokerUnityStatusSnapshot CreateRuntime(long mainThreadTick, long vmGeneration)
        {
            return new BrokerUnityStatusSnapshot
            {
                Phase = "Ready",
                CanEval = true,
                MainThreadTick = mainThreadTick,
                MainThreadTickAtUtc = DateTime.UtcNow,
                IsPlaying = Application.isPlaying,
                IsPaused = false,
                IsUpdating = false,
                VmGeneration = vmGeneration
            };
        }

        private static object? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
