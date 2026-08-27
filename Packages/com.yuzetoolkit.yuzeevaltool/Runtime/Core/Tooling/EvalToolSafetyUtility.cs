#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace YuzeToolkit.Eval
{
    internal static class EvalToolSafetyUtility
    {
        public static EvalToolFunctionDescriptor Apply(string toolPath, EvalToolFunctionDescriptor function)
        {
            // Safety is intentionally declared at the tool function site.
            // This utility no longer guesses semantics from path, method name, or description text.
            return function;
        }

        public static Dictionary<string, object?> ToJson(EvalToolSafety safety)
        {
            return EvalData.Obj(
                ("flags", GetFlagNames(safety).Cast<object?>().ToList()),
                ("riskLevel", GetRiskLevel(safety)),
                ("readOnly", Has(safety, EvalToolSafety.ReadOnly)),
                ("mutatesScene", Has(safety, EvalToolSafety.MutatesScene)),
                ("mutatesProject", Has(safety, EvalToolSafety.MutatesProject)),
                ("destructive", Has(safety, EvalToolSafety.Destructive)),
                ("requiresConfirmation", Has(safety, EvalToolSafety.RequiresConfirmation)),
                ("triggersReload", Has(safety, EvalToolSafety.TriggersReload)),
                ("reflectionDangerous", Has(safety, EvalToolSafety.ReflectionDangerous)),
                ("networkService", Has(safety, EvalToolSafety.NetworkService)),
                ("longRunning", Has(safety, EvalToolSafety.LongRunning)),
                ("mutatesEditorState", Has(safety, EvalToolSafety.MutatesEditorState)),
                ("persistsData", Has(safety, EvalToolSafety.PersistsData)),
                ("mutatesRuntimeState", Has(safety, EvalToolSafety.MutatesRuntimeState))
            );
        }

        public static string GetRiskLevel(EvalToolSafety safety)
        {
            if (Has(safety, EvalToolSafety.Destructive) ||
                Has(safety, EvalToolSafety.ReflectionDangerous))
                return "dangerous";
            if (Has(safety, EvalToolSafety.MutatesProject) ||
                Has(safety, EvalToolSafety.TriggersReload) ||
                Has(safety, EvalToolSafety.NetworkService) ||
                Has(safety, EvalToolSafety.LongRunning) ||
                Has(safety, EvalToolSafety.PersistsData))
                return "high";
            if (Has(safety, EvalToolSafety.MutatesScene) ||
                Has(safety, EvalToolSafety.MutatesEditorState) ||
                Has(safety, EvalToolSafety.MutatesRuntimeState))
                return "medium";
            return "low";
        }

        public static void ValidateDeclared(EvalToolSafety safety, string context)
        {
            const EvalToolSafety knownFlags =
                EvalToolSafety.ReadOnly |
                EvalToolSafety.MutatesScene |
                EvalToolSafety.MutatesProject |
                EvalToolSafety.Destructive |
                EvalToolSafety.RequiresConfirmation |
                EvalToolSafety.TriggersReload |
                EvalToolSafety.ReflectionDangerous |
                EvalToolSafety.NetworkService |
                EvalToolSafety.LongRunning |
                EvalToolSafety.MutatesEditorState |
                EvalToolSafety.PersistsData |
                EvalToolSafety.MutatesRuntimeState;
            const EvalToolSafety incompatibleWithReadOnly =
                EvalToolSafety.MutatesScene |
                EvalToolSafety.MutatesProject |
                EvalToolSafety.Destructive |
                EvalToolSafety.TriggersReload |
                EvalToolSafety.MutatesEditorState |
                EvalToolSafety.PersistsData |
                EvalToolSafety.MutatesRuntimeState;

            if (safety == EvalToolSafety.Unspecified)
                throw new InvalidOperationException($"{context} must declare at least one safety flag.");
            if ((safety & ~knownFlags) != 0)
                throw new InvalidOperationException($"{context} contains an unknown safety flag value.");
            if (Has(safety, EvalToolSafety.ReadOnly) && (safety & incompatibleWithReadOnly) != 0)
                throw new InvalidOperationException($"{context} cannot combine ReadOnly with mutation safety flags.");
            if (Has(safety, EvalToolSafety.Destructive) && !Has(safety, EvalToolSafety.RequiresConfirmation))
                throw new InvalidOperationException($"{context} must require confirmation when it is destructive.");
        }

        private static bool Has(EvalToolSafety safety, EvalToolSafety flag) => (safety & flag) != 0;

        private static IEnumerable<string> GetFlagNames(EvalToolSafety safety)
        {
            foreach (EvalToolSafety flag in Enum.GetValues(typeof(EvalToolSafety)))
            {
                if (flag == EvalToolSafety.Unspecified) continue;
                if (Has(safety, flag)) yield return flag.ToString();
            }
        }

    }
}
