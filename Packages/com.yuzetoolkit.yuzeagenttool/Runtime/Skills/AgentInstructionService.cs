#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YuzeToolkit.UnityAgent
{
    public sealed class AgentSkillInfo
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string RootId { get; set; } = string.Empty;

        public string FilePath { get; set; } = string.Empty;
    }

    public sealed class AgentInstructionSnapshot
    {
        public string Prompt { get; set; } = string.Empty;

        public IReadOnlyList<AgentSkillInfo> Skills { get; set; } = Array.Empty<AgentSkillInfo>();
    }

    public sealed class AgentResolvedPath
    {
        public string Id { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;
    }

    public sealed class AgentInstructionService
    {
        private const int MaxInstructionCharacters = 1_000_000;
        private const int MaxFrontMatterCharacters = 65_536;
        private const int MaxSkillCount = 2_048;

        public Task<AgentInstructionSnapshot> LoadAsync(
            AgentSettingsDocument settings,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            return Task.Run(() => Load(settings, workingDirectory, cancellationToken), cancellationToken);
        }

        public Task<IReadOnlyList<AgentSkillInfo>> ListSkillsAsync(
            AgentSettingsDocument settings,
            CancellationToken cancellationToken)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            return Task.Run<IReadOnlyList<AgentSkillInfo>>(
                () => ScanSkills(ResolveSkillRoots(settings), cancellationToken), cancellationToken);
        }

        public async Task<string> ReadSkillFileAsync(
            AgentSettingsDocument settings,
            string skillIdOrName,
            string relativePath,
            CancellationToken cancellationToken)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrWhiteSpace(skillIdOrName))
                throw new ArgumentException("Skill id or name is required.", nameof(skillIdOrName));
            var skills = await ListSkillsAsync(settings, cancellationToken).ConfigureAwait(false);
            var matches = skills.Where(skill =>
                    string.Equals(skill.Id, skillIdOrName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(skill.Name, skillIdOrName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0) throw new FileNotFoundException($"Skill '{skillIdOrName}' was not found.");
            if (matches.Count > 1)
                throw new InvalidOperationException(
                    $"Skill name '{skillIdOrName}' is ambiguous. Use one of: {string.Join(", ", matches.Select(skill => skill.Id))}");
            var skillDirectory = Path.GetDirectoryName(matches[0].FilePath)
                                 ?? throw new InvalidOperationException("Skill file has no parent directory.");
            var requested = string.IsNullOrWhiteSpace(relativePath) ? "SKILL.md" : relativePath;
            if (Path.IsPathRooted(requested))
                throw new InvalidOperationException("Skill resource path must be relative to the Skill directory.");
            var target = Path.GetFullPath(Path.Combine(skillDirectory, requested));
            if (!IsSameOrDescendant(target, skillDirectory))
                throw new InvalidOperationException("Skill relative path escapes the Skill directory.");
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(target)) throw new FileNotFoundException("Skill resource was not found.", target);
                EnsureNoReparsePoints(skillDirectory, target);
                return ReadTextLimited(target, MaxInstructionCharacters, "Skill resource", cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
        }

        public IReadOnlyList<AgentResolvedPath> ResolveAgentsRoots(AgentSettingsDocument settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var configured = ResolveConfiguredRoots(settings.AgentsRoots, "AGENTS.md", isSkillRoot: false);
            return AgentPaths.IsEditor
                ? configured
                : CombineRoots(configured, ReadPackagedRoots().AgentsRoots);
        }

        public IReadOnlyList<AgentResolvedPath> ResolveSkillRoots(AgentSettingsDocument settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var configured = ResolveConfiguredRoots(settings.SkillRoots, "Skill", isSkillRoot: true);
            return AgentPaths.IsEditor
                ? configured
                : CombineRoots(configured, ReadPackagedRoots().SkillRoots);
        }

        private static IReadOnlyList<AgentResolvedPath> ResolveConfiguredRoots(
            IReadOnlyList<AgentPathLocation>? locations,
            string kind,
            bool isSkillRoot)
        {
            if (locations == null)
                throw new InvalidDataException($"{kind} roots collection is null.");
            var roots = new List<AgentResolvedPath>();
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var location in locations)
            {
                if (location == null) throw new InvalidDataException($"{kind} roots cannot contain null entries.");
                AgentPaths.Validate(location);
                if (!usedIds.Add(location.Id))
                    throw new InvalidDataException($"Duplicate {kind} root id '{location.Id}'.");
                if (!IsScopeEnabled(location.Scope)) continue;
                var resolved = isSkillRoot ? AgentPaths.ResolveSkill(location) : AgentPaths.Resolve(location);
                // The first entry wins when multiple portable locations resolve to the same directory.
                if (roots.Any(existing => AgentPaths.PathsEqual(existing.Path, resolved))) continue;
                roots.Add(new AgentResolvedPath
                {
                    Id = NormalizeRootId(location.Id),
                    Path = resolved
                });
            }
            return roots;
        }

        private static bool IsScopeEnabled(AgentPathScope scope)
        {
            return AgentPaths.IsEditor
                ? scope is AgentPathScope.EditorOnly or AgentPathScope.All
                : scope is AgentPathScope.PlayerOnly or AgentPathScope.All;
        }

        private static IReadOnlyList<AgentResolvedPath> CombineRoots(
            IReadOnlyList<AgentResolvedPath> configured,
            IReadOnlyList<AgentResolvedPath> embedded)
        {
            var result = new List<AgentResolvedPath>(configured.Count + embedded.Count);
            foreach (var root in configured)
                AddRootIfUnique(result, root);
            foreach (var root in embedded)
                AddRootIfUnique(result, root);
            return result;
        }

        private static void AddRootIfUnique(List<AgentResolvedPath> result, AgentResolvedPath root)
        {
            if (result.Any(existing => AgentPaths.PathsEqual(existing.Path, root.Path))) return;
            result.Add(root);
        }

        private static PackagedInstructionRoots ReadPackagedRoots()
        {
            var contentPath = Path.GetFullPath(Path.Combine(
                AgentPaths.GetBasePath(AgentPathBase.StreamingAssets), "UnityAgentContent"));
            var manifestPath = Path.Combine(contentPath, "manifest.json");
            if (!File.Exists(manifestPath)) return new PackagedInstructionRoots();
            var manifestText = ReadTextLimited(manifestPath, MaxInstructionCharacters, "Agent content manifest",
                CancellationToken.None);
            var manifest = AgentJson.ParseObject(manifestText);
            var schemaVersion = AgentJson.GetLong(manifest, "schemaVersion");
            if (schemaVersion == 2)
            {
                return new PackagedInstructionRoots
                {
                    AgentsRoots = ReadManifestRootList(manifest, "agentsRoots", contentPath),
                    SkillRoots = ReadManifestRootList(manifest, "skillRoots", contentPath)
                };
            }

            // Preserve compatibility with players produced by the original combined-root build processor.
            if (schemaVersion == 1)
            {
                var result = new PackagedInstructionRoots();
                foreach (var root in AgentJson.Objects(AgentJson.GetArray(manifest, "roots")))
                {
                    var resolved = ResolvePackagedRelativePath(root, contentPath);
                    var id = NormalizeRootId(AgentJson.GetString(root, "id"));
                    if (EvalData.GetBool(root, "includeAgents", true))
                        result.AgentsRoots.Add(new AgentResolvedPath { Id = id, Path = resolved });
                    if (EvalData.GetBool(root, "includeSkills", true))
                        result.SkillRoots.Add(new AgentResolvedPath
                        {
                            Id = id,
                            Path = Path.Combine(resolved, ".agents", "skills")
                        });
                }
                return result;
            }
            throw new InvalidDataException(
                $"Unsupported Agent content manifest schema version {schemaVersion}.");
        }

        private AgentInstructionSnapshot Load(
            AgentSettingsDocument settings,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            var agentsRoots = ResolveAgentsRoots(settings);
            var prompt = new StringBuilder();
            AppendBounded(prompt,
                "\n<agents_instructions priority=\"ascending; 1 is highest\">\n" +
                "Apply the instructions that match the current work. When they conflict, the lower priority number wins.\n");
            var seenAgentsFiles = new HashSet<string>(PathComparer);
            for (var rootIndex = 0; rootIndex < agentsRoots.Count; rootIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = agentsRoots[rootIndex];
                if (!Directory.Exists(root.Path)) continue;
                foreach (var agentsPath in FindAgentsFiles(root.Path, workingDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!seenAgentsFiles.Add(Path.GetFullPath(agentsPath))) continue;
                    var content = ReadTextLimited(agentsPath, MaxInstructionCharacters, "AGENTS.md",
                        cancellationToken);
                    AppendBounded(prompt,
                        "<agents priority=\"" + (rootIndex + 1) + "\" root=\"" +
                        EscapeAttribute(root.Id) + "\" path=\"" + EscapeAttribute(agentsPath) + "\">\n" + content +
                        "\n</agents>\n");
                }
            }
            AppendBounded(prompt, "</agents_instructions>\n");

            var skills = ScanSkills(ResolveSkillRoots(settings), cancellationToken);
            if (skills.Count > 0)
            {
                AppendBounded(prompt,
                    "\n<available_skills>\nWhen a listed Skill applies, use skill_read to read its complete SKILL.md before acting. " +
                    "Earlier entries have higher priority.\n");
                foreach (var skill in skills)
                {
                    var description = (skill.Description ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
                    AppendBounded(prompt, "- " + skill.Id + ": " + EscapeText(description) + "\n");
                }
                AppendBounded(prompt, "</available_skills>\n");
            }

            return new AgentInstructionSnapshot { Prompt = prompt.ToString(), Skills = skills };
        }

        private static List<string> FindAgentsFiles(string rootPath, string workingDirectory)
        {
            var root = Path.GetFullPath(rootPath);
            var result = new List<string>();
            if (AgentPath.IsReparsePoint(root))
                throw new InvalidDataException($"Symbolic-link AGENTS.md roots are not loaded: {root}");
            var rootAgents = Path.Combine(root, "AGENTS.md");
            AddAgentsFile(rootAgents, result);
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory)) return result;
            var working = Path.GetFullPath(workingDirectory);
            if (!IsSameOrDescendant(working, root)) return result;
            var relative = PathsEqual(working, root)
                ? string.Empty
                : working.Substring(EnsureTrailingSeparator(root).Length);
            var current = root;
            foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (AgentPath.IsReparsePoint(current))
                    throw new InvalidDataException(
                        $"AGENTS.md discovery does not traverse symbolic-link directories: {current}");
                AddAgentsFile(Path.Combine(current, "AGENTS.md"), result);
            }
            return result;
        }

        private static void AddAgentsFile(string path, ICollection<string> result)
        {
            if (!File.Exists(path)) return;
            if (AgentPath.IsReparsePoint(path))
                throw new InvalidDataException($"Symbolic-link AGENTS.md files are not loaded: {path}");
            result.Add(path);
        }

        private static List<AgentSkillInfo> ScanSkills(
            IReadOnlyList<AgentResolvedPath> roots,
            CancellationToken cancellationToken)
        {
            var result = new List<AgentSkillInfo>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(root.Path)) continue;
                var skillsRoot = Path.GetFullPath(root.Path);
                if (!Directory.Exists(skillsRoot)) continue;
                if (AgentPath.IsReparsePoint(skillsRoot))
                    throw new InvalidDataException($"Symbolic-link Skill roots are not loaded: {skillsRoot}");
                foreach (var path in EnumerateSkillFiles(skillsRoot, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (result.Count >= MaxSkillCount)
                        throw new InvalidDataException($"Configured roots contain more than {MaxSkillCount} Skills.");
                    var (name, description) = ReadFrontMatter(path, cancellationToken);
                    var skillDirectory = Path.GetDirectoryName(path) ?? skillsRoot;
                    if (string.IsNullOrWhiteSpace(name)) name = new DirectoryInfo(skillDirectory).Name;
                    // Root ordering is priority ordering. A same-name Skill in a later root is shadowed.
                    if (!seenNames.Add(name)) continue;
                    var relativeDirectory = PathsEqual(skillDirectory, skillsRoot)
                        ? "_root"
                        : Path.GetFullPath(skillDirectory).Substring(EnsureTrailingSeparator(skillsRoot).Length)
                            .Replace(Path.DirectorySeparatorChar, '/')
                            .Replace(Path.AltDirectorySeparatorChar, '/');
                    relativeDirectory = string.Join("/", relativeDirectory.Split('/')
                        .Select(NormalizeRootId));
                    result.Add(new AgentSkillInfo
                    {
                        Id = root.Id + "/" + relativeDirectory,
                        Name = name,
                        Description = description,
                        RootId = root.Id,
                        FilePath = path
                    });
                }
            }

            var duplicate = result.GroupBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
                throw new InvalidDataException($"Duplicate Skill id '{duplicate.Key}' was discovered.");
            return result;
        }

        private static IEnumerable<string> EnumerateSkillFiles(
            string skillsRoot,
            CancellationToken cancellationToken)
        {
            var pending = new Queue<string>();
            pending.Enqueue(skillsRoot);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Dequeue();
                foreach (var path in Directory.EnumerateFiles(current, "SKILL.md", SearchOption.TopDirectoryOnly)
                             .OrderBy(path => path, PathComparer))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (AgentPath.IsReparsePoint(path))
                        throw new InvalidDataException($"Symbolic-link SKILL.md files are not loaded: {path}");
                    yield return path;
                }
                foreach (var directory in Directory.EnumerateDirectories(current)
                             .OrderBy(path => path, PathComparer))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (AgentPath.IsReparsePoint(directory))
                        throw new InvalidDataException(
                            $"Skill discovery does not traverse symbolic-link directories: {directory}");
                    pending.Enqueue(directory);
                }
            }
        }

        private static (string Name, string Description) ReadFrontMatter(
            string path,
            CancellationToken cancellationToken)
        {
            var name = string.Empty;
            var description = string.Empty;
            using var reader = new StreamReader(path, Encoding.UTF8, true);
            var firstLine = reader.ReadLine();
            if (firstLine?.Trim() != "---") return (name, description);
            var characters = firstLine.Length;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                characters += line.Length + 1;
                if (characters > MaxFrontMatterCharacters)
                    throw new InvalidDataException($"Skill front matter exceeds {MaxFrontMatterCharacters} characters: {path}");
                if (line.Trim() == "---") return (name, description);
                var separator = line.IndexOf(':');
                if (separator <= 0) continue;
                var key = line.Substring(0, separator).Trim();
                var value = line.Substring(separator + 1).Trim().Trim('"', '\'');
                if (key == "name") name = value;
                else if (key == "description") description = value;
            }
            throw new InvalidDataException($"Skill front matter is not terminated: {path}");
        }

        private static string ReadTextLimited(
            string path,
            int maximumCharacters,
            string kind,
            CancellationToken cancellationToken)
        {
            var content = new StringBuilder(Math.Min(maximumCharacters, 16_384));
            var buffer = new char[8_192];
            using var reader = new StreamReader(path, Encoding.UTF8, true);
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (content.Length + read > maximumCharacters)
                    throw new InvalidDataException($"{kind} exceeds the {maximumCharacters:N0} character limit: {path}");
                content.Append(buffer, 0, read);
            }
            return content.ToString();
        }

        private static void EnsureNoReparsePoints(string root, string target)
        {
            var normalizedRoot = Path.GetFullPath(root);
            var normalizedTarget = Path.GetFullPath(target);
            if (!IsSameOrDescendant(normalizedTarget, normalizedRoot))
                throw new InvalidOperationException("Skill resource escapes the Skill directory.");
            var current = normalizedRoot;
            if (AgentPath.IsReparsePoint(current))
                throw new InvalidDataException($"Symbolic-link Skill directories are not loaded: {current}");
            var relative = PathsEqual(normalizedTarget, normalizedRoot)
                ? string.Empty
                : normalizedTarget.Substring(EnsureTrailingSeparator(normalizedRoot).Length);
            foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((File.Exists(current) || Directory.Exists(current)) && AgentPath.IsReparsePoint(current))
                    throw new InvalidDataException($"Symbolic-link Skill resources are not loaded: {current}");
            }
        }

        private static void AppendBounded(StringBuilder prompt, string value)
        {
            if ((long)prompt.Length + value.Length > MaxInstructionCharacters)
                throw new InvalidDataException(
                    $"Combined AGENTS.md and Skill metadata exceeds the {MaxInstructionCharacters:N0} character limit.");
            prompt.Append(value);
        }

        private static string EscapeAttribute(string value) => value
            .Replace("&", "&amp;")
            .Replace("\"", "&quot;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");

        private static string EscapeText(string value) => value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");

        private static List<AgentResolvedPath> ReadManifestRootList(
            Dictionary<string, object?> manifest,
            string propertyName,
            string contentPath)
        {
            var result = new List<AgentResolvedPath>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in AgentJson.Objects(AgentJson.GetArray(manifest, propertyName)))
            {
                var id = NormalizeRootId(AgentJson.GetString(value, "id"));
                if (!ids.Add(id))
                    throw new InvalidDataException($"Packaged Agent manifest contains duplicate root id '{id}'.");
                result.Add(new AgentResolvedPath
                {
                    Id = id,
                    Path = ResolvePackagedRelativePath(value, contentPath)
                });
            }
            return result;
        }

        private static string ResolvePackagedRelativePath(
            Dictionary<string, object?> root,
            string contentPath)
        {
            var relativePath = AgentJson.GetString(root, "path");
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
                throw new InvalidDataException("Packaged Agent content root must have a relative path.");
            AgentPaths.ValidateRelativePath(relativePath);
            var fullPath = Path.GetFullPath(Path.Combine(contentPath, relativePath));
            if (!IsSameOrDescendant(fullPath, contentPath))
                throw new InvalidDataException(
                    $"Packaged Agent content root escapes UnityAgentContent: {relativePath}");
            return fullPath;
        }

        private static string NormalizeRootId(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "root";
            var builder = new StringBuilder(value.Length);
            foreach (var character in value.Trim())
                builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_');
            return builder.Length == 0 ? "root" : builder.ToString();
        }

        private static bool PathsEqual(string first, string second) =>
            string.Equals(TrimTrailingSeparators(Path.GetFullPath(first)),
                TrimTrailingSeparators(Path.GetFullPath(second)), PathComparison);

        private static bool IsSameOrDescendant(string candidate, string root)
        {
            var normalizedCandidate = TrimTrailingSeparators(Path.GetFullPath(candidate));
            var normalizedRoot = TrimTrailingSeparators(Path.GetFullPath(root));
            return string.Equals(normalizedCandidate, normalizedRoot, PathComparison) ||
                   normalizedCandidate.StartsWith(EnsureTrailingSeparator(normalizedRoot), PathComparison);
        }

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

        private static StringComparison PathComparison =>
            Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private static StringComparer PathComparer =>
            Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        private sealed class PackagedInstructionRoots
        {
            public List<AgentResolvedPath> AgentsRoots { get; set; } = new();

            public List<AgentResolvedPath> SkillRoots { get; set; } = new();
        }
    }

    internal sealed class SkillListAgentTool : IAgentTool
    {
        private readonly AgentInstructionService _service;
        private readonly Func<AgentSettingsDocument> _getSettings;

        public SkillListAgentTool(AgentInstructionService service, Func<AgentSettingsDocument> getSettings)
        {
            _service = service;
            _getSettings = getSettings;
            Descriptor = new AgentToolDescriptor(
                "skill_list",
                "List the available project Skills and their trigger descriptions.",
                AgentToolAccess.ReadOnly,
                AgentToolArguments.ObjectSchema(new Dictionary<string, object?>()));
        }

        public AgentToolDescriptor Descriptor { get; }

        public async Task<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            var skills = await _service.ListSkillsAsync(_getSettings(), cancellationToken).ConfigureAwait(false);
            return AgentToolResult.Success(AgentJson.Stringify(skills.Select(skill => (object?)AgentJson.Object(
                ("id", skill.Id), ("name", skill.Name), ("description", skill.Description))).ToList()));
        }
    }

    internal sealed class SkillReadAgentTool : IAgentTool
    {
        private readonly AgentInstructionService _service;
        private readonly Func<AgentSettingsDocument> _getSettings;

        public SkillReadAgentTool(AgentInstructionService service, Func<AgentSettingsDocument> getSettings)
        {
            _service = service;
            _getSettings = getSettings;
            Descriptor = new AgentToolDescriptor(
                "skill_read",
                "Read the complete SKILL.md for an applicable Skill, or one file it references inside the same Skill directory.",
                AgentToolAccess.ReadOnly,
                AgentToolArguments.ObjectSchema(AgentJson.Object(
                        ("skill", AgentToolArguments.StringProperty("Skill id from skill_list, or an unambiguous name.")),
                        ("relativePath", AgentToolArguments.StringProperty(
                            "File relative to the Skill directory. Defaults to SKILL.md."))),
                    "skill"));
        }

        public AgentToolDescriptor Descriptor { get; }

        public async Task<AgentToolResult> ExecuteAsync(
            AgentToolContext context,
            Dictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            var skill = AgentToolArguments.RequiredString(arguments, "skill");
            var relative = AgentToolArguments.OptionalString(arguments, "relativePath", "SKILL.md");
            var text = await _service.ReadSkillFileAsync(_getSettings(), skill, relative, cancellationToken)
                .ConfigureAwait(false);
            return AgentToolResult.Success(text);
        }
    }
}
