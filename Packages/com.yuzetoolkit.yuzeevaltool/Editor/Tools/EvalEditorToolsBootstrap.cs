#nullable enable
using UnityEditor;

namespace YuzeToolkit.Eval
{
    [InitializeOnLoad]
    internal static class EvalEditorToolsBootstrap
    {
        static EvalEditorToolsBootstrap()
        {
            EvalToolRegistry.TryRegisterRoot(new EditorTool());
        }
    }
}
