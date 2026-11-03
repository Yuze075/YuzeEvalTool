#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace YuzeToolkit.Eval.Broker;

// All paths in this class belong to the exporting host. Resolve every existing component,
// including intermediate links; lexical prefix checks alone do not constrain symlinks.
internal sealed class WorkspaceFiles(WorkspaceDefinition definition, WorkspaceOptions options)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public string Root { get; } = Canonical(definition.RootPath);

    public string Resolve(string path, string? relativeTo = null)
    {
        if (Canonical(definition.RootPath) != Root)
            throw Error("WorkspaceChanged", "The configured project root changed; reconnect.");
        var candidate = Canonical(Path.GetFullPath(path, relativeTo ?? Root));
        if (!Inside(candidate, Root)) throw Error("PathOutsideWorkspace", "The path or symbolic link leaves this workspace.");
        return candidate;
    }

    public static string Canonical(string path) => Canonical(path,
        new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));

    private static string Canonical(string path, HashSet<string> resolvingLinks)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget != null)
            {
                var link = current;
                if (!resolvingLinks.Add(link)) throw Error("InvalidPath", "The path contains a symbolic link cycle.");
                try
                {
                    current = Canonical(entry.ResolveLinkTarget(true)?.FullName
                        ?? throw Error("InvalidPath", "The path contains an unresolved symbolic link."), resolvingLinks);
                }
                finally { resolvingLinks.Remove(link); }
            }
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    private static bool Inside(string path, string root) => path.Equals(root, PathComparison) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, PathComparison);

    public static bool SamePath(string path, string root) => Canonical(path).Equals(root, PathComparison);

    public string DirectoryPath(string path)
    {
        var resolved = Resolve(path);
        if (!Directory.Exists(resolved)) throw Error("WorkspaceUnavailable", "The requested directory does not exist.");
        return resolved;
    }

    private byte[] ReadBytes(string path)
    {
        var resolved = Resolve(path);
        using var input = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (input.Length > options.MaxFileBytes) throw Error("FileTooLarge", "The file exceeds workspace.maxFileBytes.");
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            if (output.Length + count > options.MaxFileBytes) throw Error("FileTooLarge", "The file grew beyond workspace.maxFileBytes.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public JsonObject Read(string path, int offset = 0, int length = 0)
    {
        var bytes = ReadBytes(path);
        var text = StrictUtf8.GetString(bytes);
        if (offset < 0 || offset > text.Length || length < 0) throw Error("InvalidRequest", "Invalid text range.");
        var count = Math.Min(length == 0 ? options.MaxTextCharacters : Math.Min(length, options.MaxTextCharacters), text.Length - offset);
        if (offset > 0 && offset < text.Length && char.IsLowSurrogate(text[offset]) && char.IsHighSurrogate(text[offset - 1]))
            throw Error("InvalidRequest", "The text offset splits a Unicode surrogate pair; use the returned nextOffset.");
        if (count > 0 && offset + count < text.Length && char.IsHighSurrogate(text[offset + count - 1]) && char.IsLowSurrogate(text[offset + count])) count--;
        if (count == 0 && offset < text.Length) throw Error("InvalidRequest", "The requested page must fit one complete Unicode character.");
        return new JsonObject
        {
            ["path"] = Resolve(path), ["encoding"] = "utf-8", ["version"] = Version(bytes),
            ["offset"] = offset, ["text"] = text.Substring(offset, count), ["totalCharacters"] = text.Length,
            ["complete"] = offset == 0 && count == text.Length,
            ["nextOffset"] = offset + count < text.Length ? offset + count : null
        };
    }

    public JsonArray Guidance(string path)
    {
        var directory = Resolve(path);
        if (File.Exists(directory)) directory = Path.GetDirectoryName(directory)!;
        if (!Directory.Exists(directory)) throw Error("WorkspaceUnavailable", "Guidance requires an existing target directory.");
        var parents = new Stack<string>();
        for (var current = directory;; current = Path.GetDirectoryName(current)!)
        {
            parents.Push(current);
            if (current.Equals(Root, PathComparison)) break;
        }
        var result = new JsonArray();
        while (parents.TryPop(out var current))
        {
            var candidate = Path.Combine(current, "AGENTS.override.md");
            if (!File.Exists(candidate)) candidate = Path.Combine(current, "AGENTS.md");
            if (!File.Exists(candidate)) continue;
            var document = Read(candidate);
            document["scope"] = current;
            document["kind"] = "project-guidance";
            result.Add((JsonNode)document);
        }
        return result;
    }

    public JsonObject Skills()
    {
        var skills = new JsonArray();
        var errors = new JsonArray();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var configured in definition.SkillDirectories)
        {
            string directory;
            try { directory = Resolve(configured); }
            catch (BrokerOperationException ex) { errors.Add((JsonNode)new JsonObject { ["path"] = configured, ["error"] = ex.Message }); continue; }
            if (!Directory.Exists(directory)) continue;
            foreach (var child in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
            {
                var file = Path.Combine(child, "SKILL.md");
                if (!File.Exists(file)) continue;
                try
                {
                    file = Resolve(file);
                    if (!seen.Add(file)) continue;
                    var bytes = ReadBytes(file);
                    var header = Frontmatter(StrictUtf8.GetString(bytes));
                    skills.Add((JsonNode)new JsonObject
                    {
                        ["id"] = Path.GetRelativePath(Root, file).Replace('\\', '/'),
                        ["name"] = header.Name, ["description"] = header.Description,
                        ["path"] = file, ["root"] = Path.GetDirectoryName(file), ["version"] = Version(bytes)
                    });
                }
                catch (Exception ex) when (ex is BrokerOperationException or InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or DecoderFallbackException)
                { errors.Add((JsonNode)new JsonObject { ["path"] = file, ["error"] = ex.Message }); }
            }
        }
        return new JsonObject { ["items"] = skills, ["errors"] = errors };
    }

    public JsonObject ReadSkill(string skillId, string reference, int offset, int length)
    {
        var skill = Skills()["items"]!.AsArray().OfType<JsonObject>().SingleOrDefault(s => s["id"]!.GetValue<string>() == skillId)
            ?? throw Error("SkillNotFound", "Select an id from this project's skill catalog.");
        var root = skill["root"]!.GetValue<string>();
        if (Path.IsPathFullyQualified(reference)) throw Error("InvalidRequest", "Skill references must be relative to the remote Skill directory.");
        var target = Resolve(reference.Length == 0 ? "SKILL.md" : reference, root);
        var result = Read(target, offset, length);
        result["skillId"] = skillId;
        result["skillRoot"] = root;
        return result;
    }

    public JsonObject ApplyPatch(string path, string expectedVersion, JsonArray edits)
    {
        var target = Resolve(path);
        if (edits.Count == 0) throw Error("InvalidRequest", "At least one exact replacement is required.");
        var old = File.Exists(target) ? ReadBytes(target) : [];
        if ((File.Exists(target) ? Version(old) : "missing") != expectedVersion)
            throw Error("FileVersionConflict", "The file changed. Read it and prepare a new patch; no changes were written.");
        var text = StrictUtf8.GetString(old);
        foreach (var edit in edits.OfType<JsonObject>())
        {
            var before = edit["oldText"]?.GetValue<string>() ?? throw Error("InvalidRequest", "Each edit requires oldText.");
            var after = edit["newText"]?.GetValue<string>() ?? throw Error("InvalidRequest", "Each edit requires newText.");
            if (before.Length == 0)
            {
                if (text.Length != 0) throw Error("PatchConflict", "An empty oldText is only valid for an empty or new file.");
                text = after;
                continue;
            }
            var index = text.IndexOf(before, StringComparison.Ordinal);
            if (index < 0 || text.IndexOf(before, index + before.Length, StringComparison.Ordinal) >= 0)
                throw Error("PatchConflict", "oldText must match exactly once; no changes were written.");
            text = text[..index] + after + text[(index + before.Length)..];
        }
        if (edits.Any(e => e is not JsonObject)) throw Error("InvalidRequest", "Every edit must be an object.");
        var updated = StrictUtf8.GetBytes(text);
        if (updated.Length > options.MaxFileBytes) throw Error("FileTooLarge", "Patched file exceeds workspace.maxFileBytes.");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, updated);
            if (File.Exists(target) && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(target));
            if (Resolve(path) != target || (File.Exists(target) ? Version(ReadBytes(target)) : "missing") != expectedVersion)
                throw Error("FileVersionConflict", "File changed while preparing the patch; no changes were written.");
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new JsonObject { ["path"] = target, ["version"] = Version(updated), ["bytes"] = updated.Length };
    }

    public JsonObject Image(string path)
    {
        var bytes = ReadBytes(path);
        var mime = bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "image/png"
            : bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }) ? "image/jpeg"
            : bytes.Length > 12 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP" ? "image/webp"
            : throw Error("UnsupportedImage", "Supported image formats are PNG, JPEG and WebP.");
        return new JsonObject { ["type"] = "image", ["mimeType"] = mime, ["data"] = Convert.ToBase64String(bytes) };
    }

    public JsonObject Search(string path, string query, int offset, int limit)
    {
        var root = DirectoryPath(path);
        if (offset < 0 || limit < 1 || limit > 200 || query.Length == 0)
            throw Error("InvalidRequest", "Search requires nonempty query, nonnegative offset and limit 1–200.");
        var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions
        { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
            .Where(file => Path.GetRelativePath(root, file).Contains(query, StringComparison.OrdinalIgnoreCase));
        var page = files.Skip(offset).Take(limit + 1).ToArray();
        return new JsonObject
        {
            ["paths"] = new JsonArray(page.Take(limit).Select(file => (JsonNode?)JsonValue.Create(Path.GetRelativePath(Root, Resolve(file)))).ToArray()),
            ["nextOffset"] = page.Length > limit ? offset + limit : null,
            ["searchKind"] = "path-substring"
        };
    }

    private static (string Name, string Description) Frontmatter(string text)
    {
        var lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 3 || lines[0] != "---") throw new InvalidDataException("SKILL.md requires YAML frontmatter.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var end = Array.IndexOf(lines, "---", 1);
        if (end < 0) throw new InvalidDataException("Unterminated Skill frontmatter.");
        for (var i = 1; i < end; i++)
        {
            var key = lines[i].StartsWith("name:", StringComparison.Ordinal) ? "name" :
                lines[i].StartsWith("description:", StringComparison.Ordinal) ? "description" : null;
            if (key == null) continue;
            var value = lines[i][(key.Length + 1)..].Trim();
            if (value is ">" or ">-" or "|" or "|-")
            {
                var separator = value[0] == '>' ? " " : "\n";
                var block = new List<string>();
                while (i + 1 < end && (lines[i + 1].Length == 0 || char.IsWhiteSpace(lines[i + 1][0]))) block.Add(lines[++i].Trim());
                value = string.Join(separator, block).Trim();
            }
            else if (value.StartsWith('"') && value.EndsWith('"'))
                value = System.Text.Json.JsonSerializer.Deserialize(value, BrokerJsonContext.Default.String) ?? string.Empty;
            else if (value.StartsWith('\'') && value.EndsWith('\'')) value = value[1..^1].Replace("''", "'");
            if (!values.TryAdd(key, value) || value.Length == 0) throw new InvalidDataException("Skill name and description must be unique, nonempty text scalars.");
        }
        if (!values.TryGetValue("name", out var name) || !values.TryGetValue("description", out var description))
            throw new InvalidDataException("Skill requires name and description.");
        return (name, description);
    }

    public static string Version(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static BrokerOperationException Error(string code, string message) => new(code, message);
}
