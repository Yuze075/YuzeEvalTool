#nullable enable
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

namespace YuzeToolkit.Eval
{
#if UNITY_EDITOR
    [InitializeOnLoad]
#endif
    internal static class EvalRuntimeToolsBootstrap
    {
#if UNITY_EDITOR
        static EvalRuntimeToolsBootstrap()
        {
            RegisterTools();
        }
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RegisterToolsOnLoad()
        {
            RegisterTools();
        }

        private static void RegisterTools()
        {
            EvalToolRegistry.TryRegisterRoot(new RuntimeTool());
            EvalToolRegistry.TryRegisterRoot(new ToolManagerTool());
        }
    }
}
