#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;
using YuzeToolkit.Eval;

namespace YuzeToolkit.Agent
{
    /// <summary>
    /// Resolves portable Agent paths. A configured relative path is always interpreted relative
    /// to its selected stable base; absolute values are rejected instead of being silently used.
    /// </summary>
    public static class AgentPaths
    {
        public const string SettingsDirectoryName = ".unityagenttool";
        public const string SkillDirectoryName = ".agents/skills";
        public const string SettingsFileName = "settings.json";
        public const string ProviderSettingsFileName = "providers.json";
        public const string AgentConversationsFolderName = "AgentConversations";
        public const string CommandLineHistoryFolderName = "CommandLineHistory";
        private static readonly object SnapshotLock = new();
        private static UnityPathSnapshot? _snapshot;

        public static string ProjectRoot => Snapshot.ProjectRoot;

        public static bool IsEditor => Snapshot.IsEditor;

        public static RuntimePlatform RuntimePlatform => Snapshot.RuntimePlatform;

        public static string SettingsRoot => GetBasePath(AgentPathBase.PersistentData);

        internal static string LegacySettingsRoot => Path.GetFullPath(Snapshot.PersistentData);

        /// <summary>
        /// Capture Unity-owned path properties once on Unity's main thread. All later path
        /// resolution is pure .NET and is therefore safe inside storage and discovery workers.
        /// </summary>
        public static void CaptureUnityPathSnapshot()
        {
            lock (SnapshotLock)
            {
                if (_snapshot != null) return;
                if (!MainThreadDispatcher.IsMainThread &&
                    SynchronizationContext.Current?.GetType().FullName !=
                    "UnityEngine.UnitySynchronizationContext")
                    throw new InvalidOperationException(
                        "Yuze Agent Tool paths must first be initialized on the Unity main thread.");
                _snapshot = new UnityPathSnapshot(
                    Path.GetFullPath(ToolUtilities.GetProjectRoot()),
                    Path.GetFullPath(Application.persistentDataPath),
                    Path.GetFullPath(Application.temporaryCachePath),
                    Application.streamingAssetsPath,
                    Application.isEditor,
                    Application.platform);
            }
        }

        public static string Resolve(AgentPathLocation location)
        {
            return Resolve(location, string.Empty);
        }

        public static string ResolveSkill(AgentPathLocation location)
        {
            return Resolve(location, SkillDirectoryName);
        }

        private static string Resolve(AgentPathLocation location, string fixedRelativePath)
        {
            if (location == null) throw new ArgumentNullException(nameof(location));
            Validate(location, nameof(location));
            var basePath = GetBasePath(location.BasePath, location.UseUnityAgentToolDirectory);
            if (!string.IsNullOrEmpty(fixedRelativePath))
                basePath = Path.GetFullPath(Path.Combine(basePath, NormalizeRelativePath(fixedRelativePath)));
            return string.IsNullOrEmpty(location.RelativePath)
                ? basePath
                : Path.GetFullPath(Path.Combine(basePath, NormalizeRelativePath(location.RelativePath)));
        }

        public static string GetBasePath(AgentPathBase basePath) => GetBasePath(basePath, true);

        public static string GetBasePath(AgentPathBase basePath, bool useUnityAgentToolDirectory)
        {
            if (!Enum.IsDefined(typeof(AgentPathBase), basePath))
                throw new ArgumentOutOfRangeException(nameof(basePath), basePath, "Unknown Agent path base.");

            var value = basePath switch
            {
                AgentPathBase.ProjectRoot => Snapshot.ProjectRoot,
                AgentPathBase.PersistentData => Snapshot.PersistentData,
                AgentPathBase.UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                AgentPathBase.Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                AgentPathBase.LocalApplicationData =>
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AgentPathBase.RoamingApplicationData =>
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AgentPathBase.TemporaryCache => Snapshot.TemporaryCache,
                AgentPathBase.StreamingAssets => Snapshot.StreamingAssets,
                _ => throw new ArgumentOutOfRangeException(nameof(basePath), basePath, "Unknown Agent path base.")
            };
            if (string.IsNullOrWhiteSpace(value))
                throw new DirectoryNotFoundException($"The operating system did not provide a path for {basePath}.");
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile && !LooksLikeWindowsDrive(value))
                throw new PlatformNotSupportedException(
                    $"{basePath} is not exposed as a local file-system path on this platform: {value}");
            return useUnityAgentToolDirectory
                ? Path.GetFullPath(Path.Combine(value, SettingsDirectoryName))
                : Path.GetFullPath(value);
        }

        public static void Validate(AgentPathLocation location, string parameterName = "location")
        {
            if (location == null) throw new ArgumentNullException(parameterName);
            if (string.IsNullOrWhiteSpace(location.Id))
                throw new ArgumentException("Agent path location requires an id.", parameterName);
            foreach (var character in location.Id)
            {
                if (!char.IsLetterOrDigit(character) && character is not '-' and not '_' and not '.')
                    throw new ArgumentException(
                        $"Agent path location '{location.Id}' contains unsupported id characters.", parameterName);
            }
            if (!Enum.IsDefined(typeof(AgentPathBase), location.BasePath))
                throw new ArgumentException($"Agent path location '{location.Id}' has an unknown base.", parameterName);
            if (!Enum.IsDefined(typeof(AgentPathScope), location.Scope))
                throw new ArgumentException($"Agent path location '{location.Id}' has an unknown scope.", parameterName);
            ValidateRelativePath(location.RelativePath, parameterName);
        }

        public static void ValidateRelativePath(string? relativePath, string parameterName = "relativePath")
        {
            if (relativePath == null)
                throw new ArgumentNullException(parameterName);
            if (relativePath.IndexOf('\0') >= 0)
                throw new ArgumentException("Agent relative paths cannot contain null characters.", parameterName);
            if (Path.IsPathRooted(relativePath) || LooksLikeWindowsDrive(relativePath) ||
                relativePath.StartsWith("//", StringComparison.Ordinal) ||
                relativePath.StartsWith("\\", StringComparison.Ordinal))
                throw new ArgumentException("Agent path values must be relative to the selected base.", parameterName);
            try
            {
                _ = Path.GetFullPath(Path.Combine(Path.GetTempPath(), NormalizeRelativePath(relativePath)));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new ArgumentException("Agent relative path is not a valid file-system path.", parameterName,
                    exception);
            }
        }

        public static bool PathsEqual(string first, string second) =>
            string.Equals(TrimTrailingSeparators(Path.GetFullPath(first)),
                TrimTrailingSeparators(Path.GetFullPath(second)), PathComparison);

        public static bool IsSameOrDescendant(string candidate, string root)
        {
            var normalizedCandidate = TrimTrailingSeparators(Path.GetFullPath(candidate));
            var normalizedRoot = TrimTrailingSeparators(Path.GetFullPath(root));
            return string.Equals(normalizedCandidate, normalizedRoot, PathComparison) ||
                   normalizedCandidate.StartsWith(EnsureTrailingSeparator(normalizedRoot), PathComparison);
        }

        internal static StringComparison PathComparison =>
            Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        internal static StringComparer PathComparer =>
            Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        private static string NormalizeRelativePath(string value) => value
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        private static bool LooksLikeWindowsDrive(string value) =>
            value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':';

        private static string EnsureTrailingSeparator(string path)
        {
            if (path.Length > 0 &&
                (path[path.Length - 1] == Path.DirectorySeparatorChar ||
                 path[path.Length - 1] == Path.AltDirectorySeparatorChar)) return path;
            return path + Path.DirectorySeparatorChar;
        }

        private static string TrimTrailingSeparators(string path)
        {
            var rootLength = (Path.GetPathRoot(path) ?? string.Empty).Length;
            var length = path.Length;
            while (length > rootLength &&
                   (path[length - 1] == Path.DirectorySeparatorChar ||
                    path[length - 1] == Path.AltDirectorySeparatorChar)) length--;
            return length == path.Length ? path : path.Substring(0, length);
        }

        private static UnityPathSnapshot Snapshot
        {
            get
            {
                if (_snapshot == null) CaptureUnityPathSnapshot();
                return _snapshot ?? throw new InvalidOperationException("Yuze Agent Tool paths were not initialized.");
            }
        }

        private sealed class UnityPathSnapshot
        {
            public UnityPathSnapshot(
                string projectRoot,
                string persistentData,
                string temporaryCache,
                string streamingAssets,
                bool isEditor,
                RuntimePlatform runtimePlatform)
            {
                ProjectRoot = projectRoot;
                PersistentData = persistentData;
                TemporaryCache = temporaryCache;
                StreamingAssets = streamingAssets;
                IsEditor = isEditor;
                RuntimePlatform = runtimePlatform;
            }

            public string ProjectRoot { get; }

            public string PersistentData { get; }

            public string TemporaryCache { get; }

            public string StreamingAssets { get; }

            public bool IsEditor { get; }

            public RuntimePlatform RuntimePlatform { get; }
        }
    }
}
