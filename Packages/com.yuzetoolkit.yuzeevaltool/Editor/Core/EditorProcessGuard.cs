#nullable enable
using UnityEditor;

namespace YuzeToolkit.Eval
{
    public static class EditorProcessGuard
    {
        public static bool IsPrimaryEditorProcess => !AssetDatabase.IsAssetImportWorkerProcess();

        public static void EnsurePrimaryEditorProcess(string operation)
        {
            if (IsPrimaryEditorProcess) return;
            throw new System.InvalidOperationException(
                $"{operation} is only available in the primary Unity Editor process, not an Asset Import Worker.");
        }
    }
}
