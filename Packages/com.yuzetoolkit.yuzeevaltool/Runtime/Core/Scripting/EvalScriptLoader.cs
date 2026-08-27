#nullable enable
using System;
using Puerts;

namespace YuzeToolkit
{
    public sealed class EvalScriptLoader : ILoader, IModuleChecker
    {
        private const string ToolProtocol = "tools://";
        private static readonly DefaultLoader DefaultLoader = new();

        public static ILoader? Loader { get; set; }

        public bool FileExists(string filepath)
        {
            if (TryGetToolPath(filepath, out var toolPath))
                return IsToolIndex(toolPath) || EvalToolRegistry.ToolModuleExists(toolPath);

            return GetJsLoader().FileExists(filepath);
        }

        public string ReadFile(string filepath, out string debugpath)
        {
            if (TryGetToolPath(filepath, out var toolPath))
            {
                if (IsToolIndex(toolPath))
                {
                    debugpath = "virtual://tools://";
                    return EvalToolRegistry.GenerateIndexModuleSource();
                }

                if (EvalToolRegistry.TryGetModuleSource(toolPath, out var source))
                {
                    debugpath = "virtual://tools://" + toolPath;
                    return source;
                }

                debugpath = filepath;
                return string.Empty;
            }

            return GetJsLoader().ReadFile(filepath, out debugpath);
        }

        public bool IsESM(string filepath)
        {
            if (TryGetToolPath(filepath, out _))
                return true;

            return GetModuleChecker().IsESM(filepath);
        }

        private static ILoader GetJsLoader() => Loader ?? DefaultLoader;

        private static IModuleChecker GetModuleChecker() =>
            Loader is IModuleChecker checker ? checker : DefaultLoader;

        private static bool TryGetToolPath(string filepath, out string toolPath)
        {
            toolPath = string.Empty;
            if (string.IsNullOrWhiteSpace(filepath)) return false;
            var path = filepath.Replace('\\', '/');
            if (!path.StartsWith(ToolProtocol, StringComparison.Ordinal)) return false;
            toolPath = path.Substring(ToolProtocol.Length).Trim('/');
            return true;
        }

        private static bool IsToolIndex(string toolPath) => string.IsNullOrWhiteSpace(toolPath);
    }
}
