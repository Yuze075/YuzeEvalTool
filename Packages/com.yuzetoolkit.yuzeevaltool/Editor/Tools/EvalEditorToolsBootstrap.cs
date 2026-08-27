#nullable enable
using UnityEditor;

namespace YuzeToolkit
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
