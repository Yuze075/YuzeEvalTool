#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YuzeToolkit.UnityAgent
{
    internal static class AgentPath
    {
        public static string Resolve(AgentToolContext context, string value)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Path is required.", nameof(value));
            var combined = Path.IsPathRooted(value)
                ? value
                : Path.Combine(string.IsNullOrWhiteSpace(context.WorkingDirectory)
                    ? AgentPaths.ProjectRoot
                    : context.WorkingDirectory, value);
            var resolved = Path.GetFullPath(combined);
            if (!AgentToolPolicy.RestrictsFileSystem(context.PermissionMode)) return resolved;

            var roots = context.Surface == AgentToolSurface.Editor
                ? new[] { AgentPaths.ProjectRoot }
                : new[]
                {
                    AgentPaths.GetBasePath(AgentPathBase.PersistentData),
                    AgentPaths.GetBasePath(AgentPathBase.TemporaryCache)
                };
            foreach (var root in roots)
            {
                if (!IsSame(resolved, root) && !IsDescendant(resolved, root)) continue;
                if (TraversesReparsePoint(root, resolved))
                    throw new UnauthorizedAccessException(
                        $"Agent path crosses a symbolic link or reparse point: {resolved}");
                return resolved;
            }
            throw new UnauthorizedAccessException(
                $"Agent path is outside the allowed {context.Surface} roots for {context.PermissionMode}: {resolved}");
        }

        public static bool IsSame(string first, string second) =>
            string.Equals(NormalizeForComparison(first), NormalizeForComparison(second), PathComparison);

        public static bool IsDescendant(string candidate, string parent)
        {
            var normalizedCandidate = NormalizeForComparison(candidate);
            var normalizedParent = NormalizeForComparison(parent);
            var prefix = EndsInDirectorySeparator(normalizedParent)
                ? normalizedParent
                : normalizedParent + Path.DirectorySeparatorChar;
            return normalizedCandidate.StartsWith(prefix, PathComparison);
        }

        public static bool IsReparsePoint(string path) =>
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

        public static void EnsureSafeDeletionTarget(AgentToolContext context, string path)
        {
            var root = Path.GetPathRoot(path);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(root) && IsSame(path, root) ||
                IsSame(path, AgentPaths.ProjectRoot) ||
                !string.IsNullOrWhiteSpace(userProfile) && IsSame(path, userProfile) ||
                !string.IsNullOrWhiteSpace(context.WorkingDirectory) && IsSame(path, context.WorkingDirectory))
            {
                throw new UnauthorizedAccessException($"Agent refuses to delete a protected root path: {path}");
            }
        }

        public static Dictionary<string, object?> Describe(string path)
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                return AgentJson.Object(
                    ("path", info.FullName),
                    ("kind", "file"),
                    ("length", info.Length),
                    ("createdAtUtc", AgentJson.Utc(info.CreationTimeUtc)),
                    ("modifiedAtUtc", AgentJson.Utc(info.LastWriteTimeUtc)),
                    ("attributes", info.Attributes.ToString()));
            }
            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                return AgentJson.Object(
                    ("path", info.FullName),
                    ("kind", "directory"),
                    ("createdAtUtc", AgentJson.Utc(info.CreationTimeUtc)),
                    ("modifiedAtUtc", AgentJson.Utc(info.LastWriteTimeUtc)),
                    ("attributes", info.Attributes.ToString()));
            }
            return AgentJson.Object(("path", path), ("kind", "missing"));
        }

        private static string NormalizeForComparison(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var rootLength = (Path.GetPathRoot(fullPath) ?? string.Empty).Length;
            var length = fullPath.Length;
            while (length > rootLength && IsDirectorySeparator(fullPath[length - 1])) length--;
            return length == fullPath.Length ? fullPath : fullPath.Substring(0, length);
        }

        private static bool TraversesReparsePoint(string root, string target)
        {
            var normalizedRoot = NormalizeForComparison(root);
            var normalizedTarget = NormalizeForComparison(target);
            if (Exists(normalizedRoot) && IsReparsePoint(normalizedRoot)) return true;
            if (IsSame(normalizedRoot, normalizedTarget)) return false;
            var relative = normalizedTarget.Substring(normalizedRoot.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = normalizedRoot;
            foreach (var part in relative.Split(new[]
                     {
                         Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar
                     }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (!Exists(current)) break;
                if (IsReparsePoint(current)) return true;
            }
            return false;
        }

        private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

        private static bool EndsInDirectorySeparator(string path) =>
            path.Length > 0 && IsDirectorySeparator(path[path.Length - 1]);

        private static bool IsDirectorySeparator(char value) =>
            value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;

        private static StringComparison PathComparison =>
            Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    internal abstract class FileSystemAgentToolBase : IAgentTool
    {
        protected FileSystemAgentToolBase(AgentToolDescriptor descriptor)
        {
            Descriptor = descriptor;
        }

        public AgentToolDescriptor Descriptor { get; }

        public abstract Task<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken);

        protected static Task<AgentToolResult> Run(
            Func<CancellationToken, AgentToolResult> action,
            CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return action(cancellationToken);
            }, cancellationToken);
        }

        protected static Dictionary<string, object?> PathProperties()
        {
            return AgentJson.Object(("path", AgentToolArguments.StringProperty(
                "Absolute path or a path relative to the conversation working directory.")));
        }
    }

    internal sealed class ReadFileAgentTool : FileSystemAgentToolBase
    {
        private const int DefaultMaxCharacters = 200_000;
        private const int MaximumMaxCharacters = 1_000_000;
        private const long MaximumHashedBytes = 64_000_000;

        public ReadFileAgentTool() : base(new AgentToolDescriptor(
            "file_read_text",
            "Read a UTF-8 or BOM-identified text file and return a SHA-256 for guarded patching when the file is at most 64 MB.",
            AgentToolAccess.ReadOnly,
            AgentToolRisk.ReadOnly,
            AgentToolSurface.All,
            true,
            AgentToolArguments.ObjectSchema(AgentJson.Object(
                    ("path", AgentToolArguments.StringProperty("File path.")),
                    ("offset", AgentToolArguments.IntegerProperty("Character offset to start reading from.")),
                    ("maxCharacters", AgentToolArguments.IntegerProperty("Maximum characters returned (up to 1,000,000)."))),
                "path")))
        {
        }

        public override Task<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            var path = AgentPath.Resolve(context, AgentToolArguments.RequiredString(arguments, "path"));
            var offset = Math.Max(0, AgentToolArguments.OptionalInt(arguments, "offset", 0));
            var maxCharacters = Math.Min(MaximumMaxCharacters,
                Math.Max(1, AgentToolArguments.OptionalInt(arguments, "maxCharacters", DefaultMaxCharacters)));
            return Run(token =>
            {
                if (!File.Exists(path)) return AgentToolResult.Error($"File does not exist: {path}");
                var content = new StringBuilder(Math.Min(maxCharacters, 16_384));
                var buffer = new char[8_192];
                long scannedCharacters = 0;
                var truncated = false;
                using (var reader = new StreamReader(path, Encoding.UTF8, true))
                {
                    int read;
                    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        var chunkStart = scannedCharacters;
                        scannedCharacters += read;
                        if (content.Length >= maxCharacters || scannedCharacters <= offset) continue;
                        var start = (int)Math.Max(0L, (long)offset - chunkStart);
                        var count = Math.Min(read - start, maxCharacters - content.Length);
                        if (count > 0) content.Append(buffer, start, count);
                        if (content.Length < maxCharacters) continue;
                        truncated = scannedCharacters > (long)offset + content.Length || reader.Peek() >= 0;
                        if (truncated) break;
                    }
                }

                var actualOffset = Math.Min((long)offset, scannedCharacters);
                var fileLength = new FileInfo(path).Length;
                var hash = fileLength <= MaximumHashedBytes ? ComputeSha256(path, token) : null;
                return AgentToolResult.Success(AgentJson.Stringify(AgentJson.Object(
                    ("path", path),
                    ("offset", actualOffset),
                    ("characters", content.Length),
                    ("scannedCharacters", scannedCharacters),
                    ("totalCharacters", truncated ? null : (object?)scannedCharacters),
                    ("truncated", truncated),
                    ("sha256", hash),
                    ("sha256UnavailableReason", hash == null
                        ? $"File exceeds the {MaximumHashedBytes:N0}-byte hashing limit."
                        : string.Empty),
                    ("content", content.ToString()))));
            }, cancellationToken);
        }

        internal static string ComputeSha256(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(stream);
            cancellationToken.ThrowIfCancellationRequested();
            return string.Concat(hash.Select(value => value.ToString("x2")));
        }
    }

    internal sealed class ListDirectoryAgentTool : FileSystemAgentToolBase
    {
        private const int MaximumEntries = 10_000;

        public ListDirectoryAgentTool() : base(new AgentToolDescriptor(
            "directory_list",
            "List files and folders in a directory. Recursive listing does not traverse symbolic-link directories.",
            AgentToolAccess.ReadOnly,
            AgentToolRisk.ReadOnly,
            AgentToolSurface.All,
            true,
            AgentToolArguments.ObjectSchema(AgentJson.Object(
                    ("path", AgentToolArguments.StringProperty("Directory path.")),
                    ("recursive", AgentToolArguments.BooleanProperty("Recursively enumerate descendants.")),
                    ("maxEntries", AgentToolArguments.IntegerProperty("Maximum entries returned (up to 10,000)."))),
                "path")))
        {
        }

        public override Task<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            var path = AgentPath.Resolve(context, AgentToolArguments.RequiredString(arguments, "path"));
            var recursive = AgentToolArguments.OptionalBool(arguments, "recursive");
            var maxEntries = Math.Min(MaximumEntries,
                Math.Max(1, AgentToolArguments.OptionalInt(arguments, "maxEntries", 1000)));
            return Run(token =>
            {
                if (!Directory.Exists(path)) return AgentToolResult.Error($"Directory does not exist: {path}");
                var entries = EnumerateEntries(path, recursive, maxEntries + 1, token);
                var truncated = entries.Count > maxEntries;
                if (truncated) entries.RemoveAt(entries.Count - 1);
                return AgentToolResult.Success(AgentJson.Stringify(AgentJson.Object(
                    ("path", path),
                    ("recursive", recursive),
                    ("truncated", truncated),
                    ("entries", entries.Select(entry => (object?)AgentPath.Describe(entry)).ToList()))));
            }, cancellationToken);
        }

        private static List<string> EnumerateEntries(
            string root,
            bool recursive,
            int limit,
            CancellationToken cancellationToken)
        {
            var result = new List<string>(Math.Min(limit, 1024));
            var pendingDirectories = new Queue<string>();
            pendingDirectories.Enqueue(root);
            while (pendingDirectories.Count > 0 && result.Count < limit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pendingDirectories.Dequeue();
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.Add(entry);
                    if (result.Count >= limit) break;
                    if (recursive && Directory.Exists(entry) && !AgentPath.IsReparsePoint(entry))
                        pendingDirectories.Enqueue(entry);
                }
                if (!recursive) break;
            }
            return result;
        }
    }

    internal sealed class FileInfoAgentTool : FileSystemAgentToolBase
    {
        public FileInfoAgentTool() : base(new AgentToolDescriptor(
            "path_info",
            "Get file or directory metadata without modifying it.",
            AgentToolAccess.ReadOnly,
            AgentToolRisk.ReadOnly,
            AgentToolSurface.All,
            true,
            AgentToolArguments.ObjectSchema(PathProperties(), "path")))
        {
        }

        public override Task<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            var path = AgentPath.Resolve(context, AgentToolArguments.RequiredString(arguments, "path"));
            return Run(_ => AgentToolResult.Success(AgentJson.Stringify(AgentPath.Describe(path))), cancellationToken);
        }
    }

    internal sealed class WriteFileAgentTool : FileSystemAgentToolBase
    {
        public WriteFileAgentTool() : base(new AgentToolDescriptor(
            "file_write_text",
            "Create, overwrite or append a UTF-8 text file. Parent directories can be created automatically.",
            AgentToolAccess.Write,
            AgentToolRisk.WorkspaceWrite,
            AgentToolSurface.All,
            false,
            AgentToolArguments.ObjectSchema(AgentJson.Object(
                    ("path", AgentToolArguments.StringProperty("File path.")),
                    ("content", AgentToolArguments.StringProperty("Complete text content to write.")),
                    ("append", AgentToolArguments.BooleanProperty("Append instead of overwriting.")),
                    ("createParent", AgentToolArguments.BooleanProperty("Create missing parent directories."))),
                "path", "content")))
        {
        }

        public override Task<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            var path = AgentPath.Resolve(context, AgentToolArguments.RequiredString(arguments, "path"));
            var content = AgentToolArguments.RequiredText(arguments, "content");
            var append = AgentToolArguments.OptionalBool(arguments, "append");
            var createParent = AgentToolArguments.OptionalBool(arguments, "createParent", true);
            return Run(token =>
            {
                var parent = Path.GetDirectoryName(path);
                if (createParent && !string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                token.ThrowIfCancellationRequested();
                var encoding = new UTF8Encoding(false);
                if (append) File.AppendAllText(path, content, encoding); else File.WriteAllText(path, content, encoding);
                return AgentToolResult.Success(AgentJson.Stringify(AgentPath.Describe(path)));
            }, cancellationToken);
        }
    }

    internal sealed class ApplyPatchAgentTool : FileSystemAgentToolBase
    {
        private const int MaximumEdits = 128;
        private const int MaximumDiffCharacters = 12_000;
        private const long MaximumPatchBytes = 16_000_000;

        public ApplyPatchAgentTool() : base(new AgentToolDescriptor(
            "file_apply_patch",
            "Apply exact text replacements to an existing UTF-8 file. The SHA-256 precondition prevents overwriting concurrent changes.",
            AgentToolAccess.Write,
            AgentToolRisk.WorkspaceWrite,
            AgentToolSurface.All,
            false,
            AgentToolArguments.ObjectSchema(AgentJson.Object(
                    ("path", AgentToolArguments.StringProperty("Existing UTF-8 file path.")),
                    ("expectedSha256", AgentToolArguments.StringProperty(
                        "Lowercase SHA-256 returned by file_read_text.")),
                    ("edits", AgentJson.Object(
                        ("type", "array"),
                        ("minItems", 1),
                        ("maxItems", MaximumEdits),
                        ("description", "Ordered exact text replacements."),
                        ("items", AgentToolArguments.ObjectSchema(AgentJson.Object(
                                ("oldText", AgentToolArguments.StringProperty("Exact non-empty text to replace.")),
                                ("newText", AgentToolArguments.StringProperty("Replacement text.")),
                                ("expectedOccurrences", AgentToolArguments.IntegerProperty(
                                    "Required non-overlapping occurrence count before this edit.", 1))),
                            "oldText", "newText"))))),
                "path", "expectedSha256", "edits")))
        {
        }

        public override Task<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            var path = AgentPath.Resolve(context, AgentToolArguments.RequiredString(arguments, "path"));
            var expectedHash = AgentToolArguments.RequiredString(arguments, "expectedSha256").Trim().ToLowerInvariant();
            var edits = AgentToolArguments.RequiredObjects(arguments, "edits");
            if (edits.Count > MaximumEdits)
                throw new ArgumentException($"Patch cannot contain more than {MaximumEdits} edits.");
            if (expectedHash.Length != 64 || expectedHash.Any(character =>
                    character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                throw new ArgumentException("expectedSha256 must be a lowercase 64-character SHA-256 value.");

            return Run(token => Apply(path, expectedHash, edits, token), cancellationToken);
        }

        internal static AgentToolResult Apply(
            string path,
            string expectedHash,
            IReadOnlyList<Dictionary<string, object?>> edits,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(path)) return AgentToolResult.Error($"File does not exist: {path}");
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > MaximumPatchBytes)
                    return AgentToolResult.Error(
                        $"Patch file exceeds the {MaximumPatchBytes:N0}-byte limit: {path}");
                using var snapshot = new MemoryStream((int)stream.Length);
                var buffer = new byte[8192];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    snapshot.Write(buffer, 0, read);
                    if (snapshot.Length > MaximumPatchBytes)
                        return AgentToolResult.Error(
                            $"Patch file exceeds the {MaximumPatchBytes:N0}-byte limit: {path}");
                }
                bytes = snapshot.ToArray();
            }
            var actualHash = ComputeSha256(bytes);
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
                return AgentToolResult.Error(
                    $"File changed since it was read. Expected SHA-256 {expectedHash}, actual {actualHash}.");

            var hadUtf8Bom = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;
            string before;
            try
            {
                var offset = hadUtf8Bom ? 3 : 0;
                before = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
            }
            catch (DecoderFallbackException)
            {
                return AgentToolResult.Error($"File is not valid UTF-8 and was not changed: {path}");
            }
            var after = before;
            var applied = new List<object?>(edits.Count);
            for (var index = 0; index < edits.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var edit = edits[index];
                var oldText = AgentToolArguments.RequiredText(edit, "oldText");
                var newText = AgentToolArguments.RequiredText(edit, "newText");
                var expectedOccurrences = AgentToolArguments.OptionalInt(edit, "expectedOccurrences", 1);
                if (oldText.Length == 0)
                    return AgentToolResult.Error($"Patch edit {index} has an empty oldText.");
                if (expectedOccurrences < 1)
                    return AgentToolResult.Error($"Patch edit {index} expectedOccurrences must be positive.");
                var occurrences = CountOccurrences(after, oldText);
                if (occurrences != expectedOccurrences)
                {
                    return AgentToolResult.Error(
                        $"Patch edit {index} expected {expectedOccurrences} occurrence(s), found {occurrences}; file was not changed.");
                }
                after = after.Replace(oldText, newText);
                applied.Add(AgentJson.Object(
                    ("index", index),
                    ("occurrences", occurrences),
                    ("oldCharacters", oldText.Length),
                    ("newCharacters", newText.Length)));
            }

            if (string.Equals(before, after, StringComparison.Ordinal))
                return AgentToolResult.Error("Patch produced no file content change.");
            WriteAtomic(path, after, new UTF8Encoding(hadUtf8Bom), cancellationToken);
            var resultHash = ReadFileAgentTool.ComputeSha256(path, cancellationToken);
            return AgentToolResult.Success(AgentJson.Stringify(AgentJson.Object(
                ("path", path),
                ("beforeSha256", actualHash),
                ("afterSha256", resultHash),
                ("edits", applied),
                ("diff", CreateBoundedDiff(path, before, after)))));
        }

        private static int CountOccurrences(string text, string value)
        {
            var count = 0;
            var offset = 0;
            while (offset <= text.Length - value.Length)
            {
                var found = text.IndexOf(value, offset, StringComparison.Ordinal);
                if (found < 0) break;
                count++;
                offset = found + value.Length;
            }
            return count;
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using var sha256 = SHA256.Create();
            return string.Concat(sha256.ComputeHash(bytes).Select(value => value.ToString("x2")));
        }

        private static void WriteAtomic(
            string path,
            string content,
            Encoding encoding,
            CancellationToken cancellationToken)
        {
            var parent = Path.GetDirectoryName(path) ??
                         throw new InvalidOperationException("Patch path has no parent directory.");
            var temporary = Path.Combine(parent, "." + Path.GetFileName(path) + ".unityagent-" +
                                                  Guid.NewGuid().ToString("N") + ".tmp");
            var backup = temporary + ".bak";
            try
            {
                File.WriteAllText(temporary, content, encoding);
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    File.Replace(temporary, path, backup);
                }
                catch (PlatformNotSupportedException)
                {
                    ReplaceWithoutFileReplace(path, temporary, backup);
                }
                if (File.Exists(backup)) File.Delete(backup);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static void ReplaceWithoutFileReplace(string path, string temporary, string backup)
        {
            File.Move(path, backup);
            try
            {
                File.Move(temporary, path);
            }
            catch
            {
                if (!File.Exists(path) && File.Exists(backup)) File.Move(backup, path);
                throw;
            }
        }

        private static string CreateBoundedDiff(string path, string before, string after)
        {
            var prefix = 0;
            var maximumPrefix = Math.Min(before.Length, after.Length);
            while (prefix < maximumPrefix && before[prefix] == after[prefix]) prefix++;
            var beforeSuffix = before.Length;
            var afterSuffix = after.Length;
            while (beforeSuffix > prefix && afterSuffix > prefix &&
                   before[beforeSuffix - 1] == after[afterSuffix - 1])
            {
                beforeSuffix--;
                afterSuffix--;
            }
            var contextStart = prefix == 0 ? 0 : before.LastIndexOf('\n', prefix - 1);
            if (contextStart < 0) contextStart = 0;
            var beforeEnd = before.IndexOf('\n', beforeSuffix);
            if (beforeEnd < 0) beforeEnd = before.Length;
            var afterEnd = after.IndexOf('\n', afterSuffix);
            if (afterEnd < 0) afterEnd = after.Length;
            var beforeChunk = before.Substring(contextStart, beforeEnd - contextStart);
            var afterChunk = after.Substring(contextStart, afterEnd - contextStart);
            var diff = new StringBuilder()
                .Append("--- ").AppendLine(path)
                .Append("+++ ").AppendLine(path)
                .AppendLine("@@ changed region @@");
            foreach (var line in NormalizeLines(beforeChunk)) diff.Append('-').AppendLine(line);
            foreach (var line in NormalizeLines(afterChunk)) diff.Append('+').AppendLine(line);
            var value = diff.ToString();
            return value.Length <= MaximumDiffCharacters
                ? value
                : value.Substring(0, MaximumDiffCharacters) + "\n... diff truncated ...";
        }

        private static IEnumerable<string> NormalizeLines(string value) =>
            value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    internal sealed class CreateDirectoryAgentTool : FileSystemAgentToolBase
    {
        public CreateDirectoryAgentTool() : base(new AgentToolDescriptor(
            "directory_create",
            "Create a directory, including missing parents.",
            AgentToolAccess.Write,
            AgentToolRisk.WorkspaceWrite,
            AgentToolSurface.All,
            false,
            AgentToolArguments.ObjectSchema(PathProperties(), "path")))
        {
        }

        public override Task<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            var path = AgentPath.Resolve(context, AgentToolArguments.RequiredString(arguments, "path"));
            return Run(_ =>
            {
                Directory.CreateDirectory(path);
                return AgentToolResult.Success(AgentJson.Stringify(AgentPath.Describe(path)));
            }, cancellationToken);
        }
    }

    internal sealed class DeletePathAgentTool : FileSystemAgentToolBase
    {
        public DeletePathAgentTool() : base(new AgentToolDescriptor(
            "path_delete",
            "Delete a file or directory. Directory deletion can be recursive.",
            AgentToolAccess.Write,
            AgentToolRisk.Destructive,
            AgentToolSurface.All,
            false,
            AgentToolArguments.ObjectSchema(AgentJson.Object(
                    ("path", AgentToolArguments.StringProperty("File or directory path.")),
                    ("recursive", AgentToolArguments.BooleanProperty("Delete a non-empty directory recursively."))),
                "path")))
        {
        }

        public override Task<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            var path = AgentPath.Resolve(context, AgentToolArguments.RequiredString(arguments, "path"));
            AgentPath.EnsureSafeDeletionTarget(context, path);
            var recursive = AgentToolArguments.OptionalBool(arguments, "recursive");
            return Run(token =>
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive && !AgentPath.IsReparsePoint(path));
                }
                else
                {
                    return AgentToolResult.Error($"Path does not exist: {path}");
                }
                return AgentToolResult.Success($"Deleted: {path}");
            }, cancellationToken);
        }
    }

    internal sealed class CopyPathAgentTool : FileSystemAgentToolBase
    {
        public CopyPathAgentTool() : base(new AgentToolDescriptor(
            "path_copy",
            "Copy a file or directory to another non-overlapping path.",
            AgentToolAccess.Write,
            AgentToolRisk.WorkspaceWrite,
            AgentToolSurface.All,
            false,
            AgentToolArguments.ObjectSchema(AgentJson.Object(
                    ("source", AgentToolArguments.StringProperty("Source file or directory.")),
                    ("destination", AgentToolArguments.StringProperty("Destination path.")),
                    ("overwrite", AgentToolArguments.BooleanProperty("Replace existing destination files."))),
                "source", "destination")))
        {
        }

        public override Task<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            var source = AgentPath.Resolve(context, AgentToolArguments.RequiredString(arguments, "source"));
            var destination = AgentPath.Resolve(context, AgentToolArguments.RequiredString(arguments, "destination"));
            var overwrite = AgentToolArguments.OptionalBool(arguments, "overwrite");
            return Run(token =>
            {
                var sourceIsFile = File.Exists(source);
                var sourceIsDirectory = Directory.Exists(source);
                if (!sourceIsFile && !sourceIsDirectory)
                    return AgentToolResult.Error($"Source does not exist: {source}");
                if (AgentPath.IsSame(source, destination))
                    return AgentToolResult.Error("Source and destination resolve to the same path.");

                if (sourceIsFile)
                {
                    var parent = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                    token.ThrowIfCancellationRequested();
                    File.Copy(source, destination, overwrite);
                }
                else
                {
                    if (AgentPath.IsDescendant(destination, source) || AgentPath.IsDescendant(source, destination))
                        return AgentToolResult.Error("Source and destination directories must not overlap.");
                    if (AgentPath.IsReparsePoint(source))
                        return AgentToolResult.Error("Copying a symbolic-link directory is not supported.");
                    CopyDirectory(source, destination, overwrite, token);
                }
                return AgentToolResult.Success(AgentJson.Stringify(AgentPath.Describe(destination)));
            }, cancellationToken);
        }

        private static void CopyDirectory(
            string source,
            string destination,
            bool overwrite,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite);
            }
            foreach (var directory in Directory.EnumerateDirectories(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (AgentPath.IsReparsePoint(directory))
                    throw new InvalidOperationException($"Directory copy does not traverse symbolic links: {directory}");
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), overwrite,
                    cancellationToken);
            }
        }
    }

    internal sealed class MovePathAgentTool : FileSystemAgentToolBase
    {
        public MovePathAgentTool() : base(new AgentToolDescriptor(
            "path_move",
            "Move or rename a file or directory.",
            AgentToolAccess.Write,
            AgentToolRisk.WorkspaceWrite,
            AgentToolSurface.All,
            false,
            AgentToolArguments.ObjectSchema(AgentJson.Object(
                    ("source", AgentToolArguments.StringProperty("Source file or directory.")),
                    ("destination", AgentToolArguments.StringProperty("Destination path.")),
                    ("overwrite", AgentToolArguments.BooleanProperty("Replace an existing destination."))),
                "source", "destination")))
        {
        }

        public override Task<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            var source = AgentPath.Resolve(context, AgentToolArguments.RequiredString(arguments, "source"));
            var destination = AgentPath.Resolve(context, AgentToolArguments.RequiredString(arguments, "destination"));
            var overwrite = AgentToolArguments.OptionalBool(arguments, "overwrite");
            return Run(token =>
            {
                var sourceIsFile = File.Exists(source);
                var sourceIsDirectory = Directory.Exists(source);
                if (!sourceIsFile && !sourceIsDirectory)
                    return AgentToolResult.Error($"Source does not exist: {source}");
                if (AgentPath.IsSame(source, destination))
                    return AgentToolResult.Success(AgentJson.Stringify(AgentPath.Describe(source)));
                if (sourceIsDirectory &&
                    (AgentPath.IsDescendant(destination, source) || AgentPath.IsDescendant(source, destination)))
                    return AgentToolResult.Error("Source and destination directories must not overlap.");

                var destinationIsFile = File.Exists(destination);
                var destinationIsDirectory = Directory.Exists(destination);
                if ((destinationIsFile || destinationIsDirectory) && !overwrite)
                    return AgentToolResult.Error($"Destination already exists: {destination}");

                var parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                token.ThrowIfCancellationRequested();

                string? displacedDestination = null;
                if (destinationIsFile || destinationIsDirectory)
                {
                    displacedDestination = destination + ".unityagent-" + Guid.NewGuid().ToString("N") + ".backup";
                    if (destinationIsFile) File.Move(destination, displacedDestination);
                    else Directory.Move(destination, displacedDestination);
                }

                try
                {
                    if (sourceIsFile) File.Move(source, destination);
                    else Directory.Move(source, destination);
                }
                catch (Exception moveError) when (moveError is IOException ||
                                                  moveError is UnauthorizedAccessException ||
                                                  moveError is InvalidOperationException)
                {
                    if (displacedDestination == null) throw;
                    try
                    {
                        if (File.Exists(displacedDestination)) File.Move(displacedDestination, destination);
                        else if (Directory.Exists(displacedDestination)) Directory.Move(displacedDestination, destination);
                    }
                    catch (Exception restoreError) when (restoreError is IOException ||
                                                         restoreError is UnauthorizedAccessException ||
                                                         restoreError is InvalidOperationException)
                    {
                        throw new AggregateException(
                            $"Move failed and the previous destination could not be restored from '{displacedDestination}'.",
                            moveError, restoreError);
                    }
                    throw;
                }

                if (displacedDestination != null)
                {
                    if (File.Exists(displacedDestination)) File.Delete(displacedDestination);
                    else if (Directory.Exists(displacedDestination))
                        Directory.Delete(displacedDestination, !AgentPath.IsReparsePoint(displacedDestination));
                }
                return AgentToolResult.Success(AgentJson.Stringify(AgentPath.Describe(destination)));
            }, cancellationToken);
        }
    }
}
