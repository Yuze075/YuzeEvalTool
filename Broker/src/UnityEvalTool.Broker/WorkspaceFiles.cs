#nullable enable
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace YuzeToolkit.Eval.Broker;

// All paths in this class belong to the exporting host. Resolve every existing component,
// including intermediate links; lexical prefix checks alone do not constrain symlinks.
internal sealed class WorkspaceFiles(WorkspaceDefinition definition, WorkspaceOptions options)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
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

    private string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    public string DirectoryPath(string path)
    {
        var resolved = Resolve(path);
        if (!Directory.Exists(resolved)) throw Error("WorkspaceUnavailable", "The requested directory does not exist.");
        return resolved;
    }

    private byte[] ReadBytes(string resolved)
    {
        using var input = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (input.Length > options.MaxFileBytes) throw Error("FileTooLarge", "The file exceeds workspace.maxFileBytes.");
        using var output = new MemoryStream();
        input.CopyTo(output);
        if (output.Length > options.MaxFileBytes) throw Error("FileTooLarge", "The file grew beyond workspace.maxFileBytes.");
        return output.ToArray();
    }

    private string? CurrentVersion(string resolved) => File.Exists(resolved) ? Version(ReadBytes(resolved)) : null;

    // Reads numbered lines within a shared character budget, which the caller decrements.
    public JsonObject ReadLines(string path, int startLine, int lineCount, ref int budget)
    {
        var resolved = Resolve(path);
        var bytes = ReadBytes(resolved);
        var text = StrictUtf8.GetString(bytes);
        var lines = text.Split('\n');
        if (lines.Length > 1 && lines[^1].Length == 0) lines = lines[..^1];
        if (text.Length == 0) lines = [];
        if (startLine < 1 || lineCount < 0 || startLine > Math.Max(lines.Length, 1))
            throw Error("InvalidRequest", "startLine must be 1–" + Math.Max(lines.Length, 1) + " and lineCount nonnegative.");
        var output = new StringBuilder();
        var line = startLine;
        var last = lineCount == 0 ? lines.Length : Math.Min(lines.Length, startLine - 1 + lineCount);
        for (; line <= last; line++)
        {
            var entry = line + "\t" + lines[line - 1].TrimEnd('\r') + "\n";
            if (entry.Length > budget && output.Length > 0) break;
            output.Append(entry);
            budget -= entry.Length;
        }
        return new JsonObject
        {
            ["path"] = Relative(resolved), ["version"] = Version(bytes), ["totalLines"] = lines.Length,
            ["lineEnding"] = text.Contains("\r\n", StringComparison.Ordinal) ? "crlf" : "lf",
            ["startLine"] = startLine, ["endLine"] = line - 1, ["text"] = output.ToString(),
            ["complete"] = startLine == 1 && line > lines.Length, ["nextLine"] = line <= lines.Length ? line : null
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
        var budget = options.MaxTextCharacters;
        while (parents.TryPop(out var current))
        {
            var candidate = Path.Combine(current, "AGENTS.override.md");
            if (!File.Exists(candidate)) candidate = Path.Combine(current, "AGENTS.md");
            if (!File.Exists(candidate)) continue;
            var document = ReadLines(candidate, 1, 0, ref budget);
            document["scope"] = Relative(current);
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
            catch (BrokerOperationException ex) { errors.Add((JsonNode)new JsonObject { ["path"] = configured, ["error"] = ex.Detail }); continue; }
            if (!Directory.Exists(directory)) continue;
            foreach (var child in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
            {
                var file = Path.Combine(child, "SKILL.md");
                if (!File.Exists(file)) continue;
                try
                {
                    file = Resolve(file);
                    if (!seen.Add(file)) continue;
                    var header = Frontmatter(StrictUtf8.GetString(ReadBytes(file)));
                    skills.Add((JsonNode)new JsonObject { ["name"] = header.Name, ["description"] = header.Description, ["path"] = Relative(file) });
                }
                catch (Exception ex) when (ex is BrokerOperationException or InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or DecoderFallbackException)
                { errors.Add((JsonNode)new JsonObject { ["path"] = Relative(file), ["error"] = ex.Message }); }
            }
        }
        return new JsonObject { ["items"] = skills, ["errors"] = errors };
    }

    // Inside a git work tree, git decides which files are ignored; elsewhere every regular file counts.
    private List<string> ListFiles(string directory)
    {
        var inGit = false;
        for (var current = directory; current != null; current = Path.GetDirectoryName(current))
            if (Path.Exists(Path.Combine(current, ".git"))) { inGit = true; break; }
        if (!inGit)
            return Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(file => !Path.GetRelativePath(directory, file).Split(Path.DirectorySeparatorChar).Contains(".git")).ToList();
        var listed = Git(directory, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], projectCeiling: false);
        return listed.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct()
            .Select(relative => Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)))
            .Where(file => File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0).ToList();
    }

    public JsonObject Glob(string path, string pattern, int offset, int limit)
    {
        if (pattern.Length == 0 || offset < 0 || limit is < 1 or > 1000) throw Error("InvalidRequest", "Glob requires a pattern, nonnegative offset and limit 1–1000.");
        var directory = DirectoryPath(path);
        var matcher = GlobRegex(pattern);
        var matches = ListFiles(directory).Where(file => Matches(matcher, pattern, Path.GetRelativePath(directory, file)))
            .Select(file => (File: file, Modified: File.GetLastWriteTimeUtc(file)))
            .OrderByDescending(item => item.Modified).ThenBy(item => item.File, StringComparer.Ordinal).ToList();
        return new JsonObject
        {
            ["paths"] = new JsonArray(matches.Skip(offset).Take(limit).Select(item => (JsonNode?)JsonValue.Create(Relative(item.File))).ToArray()),
            ["total"] = matches.Count, ["nextOffset"] = offset + limit < matches.Count ? offset + limit : null
        };
    }

    public JsonObject Grep(string path, string pattern, string glob, bool ignoreCase, string mode, int contextLines, int offset, int limit)
    {
        if (mode is not ("files" or "content" or "count") || contextLines is < 0 or > 10 || offset < 0 || limit is < 1 or > 1000)
            throw Error("InvalidRequest", "outputMode must be files, content or count; contextLines 0–10; limit 1–1000.");
        Regex regex;
        try { regex = new Regex(pattern, RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), RegexTimeout); }
        catch (ArgumentException ex) { throw Error("InvalidPattern", ex.Message); }
        var directory = DirectoryPath(path);
        var filter = glob.Length == 0 ? null : GlobRegex(glob);
        var results = new List<string>();
        var needed = offset + limit + 1;
        foreach (var file in ListFiles(directory).Order(StringComparer.Ordinal))
        {
            if (results.Count >= needed) break;
            if (filter != null && !Matches(filter, glob, Path.GetRelativePath(directory, file))) continue;
            if (new FileInfo(file).Length > options.MaxFileBytes) continue;
            var bytes = File.ReadAllBytes(file);
            if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0)) continue;
            string[] lines;
            try { lines = StrictUtf8.GetString(bytes).Split('\n'); }
            catch (DecoderFallbackException) { continue; }
            var name = Relative(file);
            try
            {
                var hits = Enumerable.Range(0, lines.Length).Where(i => regex.IsMatch(lines[i].TrimEnd('\r'))).ToList();
                if (hits.Count == 0) continue;
                if (mode == "files") { results.Add(name); continue; }
                if (mode == "count") { results.Add(name + ":" + hits.Count); continue; }
                var shown = new SortedSet<int>(hits.SelectMany(i => Enumerable.Range(Math.Max(0, i - contextLines), Math.Min(lines.Length, i + contextLines + 1) - Math.Max(0, i - contextLines))));
                var matched = hits.ToHashSet();
                results.AddRange(shown.Select(i => name + (matched.Contains(i) ? ":" : "-") + (i + 1) + (matched.Contains(i) ? ":" : "-") + lines[i].TrimEnd('\r')));
            }
            catch (RegexMatchTimeoutException) { throw Error("PatternTimeout", "The regular expression took too long on " + name + "; simplify it."); }
        }
        var page = new StringBuilder();
        var returned = 0;
        foreach (var entry in results.Skip(offset).Take(limit))
        {
            if (page.Length + entry.Length + 1 > options.MaxTextCharacters && returned > 0) break;
            page.Append(entry).Append('\n');
            returned++;
        }
        return new JsonObject
        {
            ["mode"] = mode, ["results"] = page.ToString(), ["count"] = returned,
            ["nextOffset"] = results.Count > offset + returned ? offset + returned : null
        };
    }

    public JsonObject Write(string path, string content, string expectedVersion)
    {
        var target = Resolve(path);
        if ((CurrentVersion(target) ?? "missing") != expectedVersion)
            throw Error("FileVersionConflict", "The file does not match expectedVersion. Read it again; nothing was written.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        return Commit(path, target, StrictUtf8.GetBytes(content), expectedVersion);
    }

    public JsonObject Edit(string path, string? expectedVersion, JsonArray edits)
    {
        var target = Resolve(path);
        if (edits.Count == 0) throw Error("InvalidRequest", "At least one edit is required.");
        var current = CurrentVersion(target) ?? "missing";
        if (expectedVersion != null && current != expectedVersion)
            throw Error("FileVersionConflict", "The file does not match expectedVersion. Read it again; nothing was written.");
        var text = current == "missing" ? string.Empty : StrictUtf8.GetString(ReadBytes(target));
        var crlf = text.Contains("\r\n", StringComparison.Ordinal);
        foreach (var node in edits)
        {
            var edit = node as JsonObject ?? throw Error("InvalidRequest", "Every edit must be an object.");
            var before = WorkspaceService.Text(edit, "oldText");
            var after = WorkspaceService.Text(edit, "newText");
            if (crlf && !before.Contains('\r')) { before = before.Replace("\n", "\r\n"); after = after.Replace("\n", "\r\n"); }
            if (before.Length == 0)
            {
                if (text.Length != 0) throw Error("EditConflict", "An empty oldText is only valid for an empty or new file; nothing was written.");
                text = after;
                continue;
            }
            var count = Regex.Count(text, Regex.Escape(before));
            if (edit["replaceAll"]?.GetValue<bool>() == true ? count == 0 : count != 1)
                throw Error("EditConflict", "oldText occurs " + count + " times; it must occur exactly once unless replaceAll is true. Nothing was written.");
            text = text.Replace(before, after, StringComparison.Ordinal);
        }
        if (current == "missing") Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        return Commit(path, target, StrictUtf8.GetBytes(text), current);
    }

    private JsonObject Commit(string path, string target, byte[] updated, string expectedVersion)
    {
        if (updated.Length > options.MaxFileBytes) throw Error("FileTooLarge", "The result exceeds workspace.maxFileBytes.");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, updated);
            if (File.Exists(target) && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(target));
            if (Resolve(path) != target || (CurrentVersion(target) ?? "missing") != expectedVersion)
                throw Error("FileVersionConflict", "The file changed while writing; nothing was written.");
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new JsonObject { ["path"] = Relative(target), ["version"] = Version(updated), ["bytes"] = updated.Length };
    }

    // git apply checks every hunk before writing and rejects paths outside the work area or behind
    // symbolic links. The ceiling keeps a project inside a larger repository rooted at its own folder.
    public JsonObject ApplyDiff(string diff)
    {
        if (diff.Length == 0 || Encoding.UTF8.GetByteCount(diff) > options.MaxFileBytes) throw Error("InvalidRequest", "diff must be nonempty and within workspace.maxFileBytes.");
        if (!diff.EndsWith('\n')) diff += "\n";
        var paths = Git(Root, ["apply", "--numstat", "-z", "-"], true, diff).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Split('\t') is { Length: 3 } fields ? fields[2] : entry).Where(entry => entry.Length != 0).Distinct().ToList();
        Git(Root, ["apply", "--whitespace=nowarn", "-"], true, diff);
        return new JsonObject
        {
            ["files"] = new JsonArray(paths.Select(relative =>
            {
                var resolved = Resolve(relative);
                return (JsonNode)new JsonObject { ["path"] = Relative(resolved), ["version"] = CurrentVersion(resolved) ?? "deleted" };
            }).ToArray())
        };
    }

    private string Git(string directory, string[] args, bool projectCeiling, string? input = null)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = StrictUtf8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        if (projectCeiling) start.Environment["GIT_CEILING_DIRECTORIES"] = Path.GetDirectoryName(Root) ?? Root;
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        Process process;
        try { process = Process.Start(start)!; }
        catch (System.ComponentModel.Win32Exception ex) { throw Error("GitUnavailable", "git is required for this operation: " + ex.Message); }
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(input ?? string.Empty);
            process.StandardInput.Close();
            if (!process.WaitForExit(TimeSpan.FromSeconds(options.RequestTimeoutSeconds)))
            {
                process.Kill(entireProcessTree: true);
                throw Error("GitTimedOut", "git did not finish within workspace.requestTimeoutSeconds.");
            }
            process.WaitForExit();
            if (process.ExitCode != 0) throw Error(args[0] == "apply" ? "DiffRejected" : "GitFailed", error.Result.Trim() + " Nothing was written.");
            return output.Result;
        }
    }

    private static bool Matches(Regex matcher, string pattern, string relative)
    {
        relative = relative.Replace('\\', '/');
        return matcher.IsMatch(pattern.Contains('/') ? relative : relative[(relative.LastIndexOf('/') + 1)..]);
    }

    private static Regex GlobRegex(string glob)
    {
        var regex = new StringBuilder("^");
        var braces = 0;
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; regex.Append("(?:.*/)?"); }
                    else regex.Append(".*");
                    break;
                case '*': regex.Append("[^/]*"); break;
                case '?': regex.Append("[^/]"); break;
                case '{': braces++; regex.Append("(?:"); break;
                case '}' when braces > 0: braces--; regex.Append(')'); break;
                case ',' when braces > 0: regex.Append('|'); break;
                case '[':
                    var end = glob.IndexOf(']', i + 1);
                    if (end < 0) throw Error("InvalidPattern", "Unterminated [ in glob.");
                    var set = glob[(i + 1)..end];
                    regex.Append('[').Append(set.StartsWith('!') ? "^" + Regex.Escape(set[1..]) : Regex.Escape(set)).Append(']');
                    i = end;
                    break;
                default: regex.Append(Regex.Escape(c.ToString())); break;
            }
        }
        if (braces != 0) throw Error("InvalidPattern", "Unbalanced { in glob.");
        return new Regex(regex.Append('$').ToString(), RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None));
    }

    public JsonObject Image(string path)
    {
        var bytes = ReadBytes(Resolve(path));
        var mime = bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "image/png"
            : bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }) ? "image/jpeg"
            : bytes.Length > 12 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP" ? "image/webp"
            : throw Error("UnsupportedImage", "Supported image formats are PNG, JPEG and WebP.");
        return new JsonObject { ["type"] = "image", ["mimeType"] = mime, ["data"] = Convert.ToBase64String(bytes) };
    }

    private static (string Name, string Description) Frontmatter(string text)
    {
        var lines = text.TrimStart('﻿').Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 3 || lines[0] != "---") throw new InvalidDataException("SKILL.md requires YAML frontmatter.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var end = System.Array.IndexOf(lines, "---", 1);
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
