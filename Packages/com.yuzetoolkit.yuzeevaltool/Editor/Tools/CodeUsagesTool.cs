#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace YuzeToolkit
{
    [EvalTool("CodeUsages", "Bounded, read-only search for serialized uses of a C# script or member.")]
    public sealed partial class CodeUsagesTool
    {
        private const int DefaultLimit = 100;
        private const int MaxLimit = 500;
        private const int MaxCandidateAssets = 5000;
        private const int MaxSerializedOwners = 50000;
        private const int MaxPropertiesPerOwner = 5000;
        private const long MaxYamlBytes = 32L * 1024L * 1024L;

        [EvalFunction("Find bounded serialized script/member usages in explicitly scoped folders.",
            Safety = EvalToolSafety.ReadOnly | EvalToolSafety.LongRunning)]
        public Dictionary<string, object?> search(
            [EvalParameter("C# MonoScript asset path, for example Assets/Scripts/MyComponent.cs.")]
            string scriptPath,
            [EvalParameter("Required asset folder path or array of folder paths. The project root is never scanned implicitly.")]
            object folders,
            [EvalParameter("Optional serialized field, UnityEvent method, or AnimationEvent function name. Empty lists MonoBehaviour/ScriptableObject attachment points.")]
            string member = "",
            [EvalParameter("Maximum number of returned usages. Must be 1..500.")]
            int limit = DefaultLimit)
        {
            if (string.IsNullOrWhiteSpace(scriptPath))
                throw new InvalidOperationException("Argument 'scriptPath' is required.");
            if (limit < 1 || limit > MaxLimit)
                throw new InvalidOperationException($"Argument 'limit' must be between 1 and {MaxLimit}.");

            var scope = ResolveFolders(folders);
            if (!ToolUtilities.TryResolveProjectPath(scriptPath.Replace('\\', '/').Trim(), out _,
                    out var normalizedScriptPath, out var scriptPathError))
                throw new InvalidOperationException(scriptPathError);
            if (!IsAssetDatabasePath(normalizedScriptPath))
                throw new InvalidOperationException("Argument 'scriptPath' must be under Assets or Packages.");
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(normalizedScriptPath);
            if (script == null)
                throw new InvalidOperationException($"MonoScript asset '{normalizedScriptPath}' was not found.");

            var scriptType = script.GetClass();
            if (scriptType == null)
                throw new InvalidOperationException(
                    $"MonoScript '{normalizedScriptPath}' does not currently resolve to a compiled class.");

            member = member?.Trim() ?? string.Empty;
            var candidateLimit = Math.Min(MaxCandidateAssets, Math.Max(100, limit * 20));
            var ownerLimit = Math.Min(MaxSerializedOwners, Math.Max(500, limit * 100));
            var scriptGuid = AssetDatabase.AssetPathToGUID(normalizedScriptPath);
            var context = new SearchContext(
                normalizedScriptPath,
                scriptGuid,
                scriptType,
                member,
                limit,
                candidateLimit,
                ownerLimit);

            var candidates = FindCandidatePaths(
                scope,
                member.Length > 0,
                candidateLimit,
                out var candidateSearchTruncated,
                out var candidateSearch);
            foreach (var assetPath in candidates)
            {
                if (context.ShouldStop) break;
                ScanAsset(assetPath, context);
            }

            if (member.Length > 0 && !context.ResultLimitReached && !context.OwnerLimitReached)
            {
                foreach (var assetPath in candidates)
                {
                    if (context.ShouldStop) break;
                    ScanBrokenUnityEventYaml(assetPath, context);
                }
            }

            return EvalData.Obj(
                ("scriptPath", normalizedScriptPath),
                ("scriptGuid", scriptGuid),
                ("scriptType", scriptType.FullName ?? scriptType.Name),
                ("member", member),
                ("folders", scope.Cast<object?>().ToList()),
                ("limit", limit),
                ("candidateAssetLimit", candidateLimit),
                ("candidateSearch", candidateSearch),
                ("serializedOwnerLimit", ownerLimit),
                ("propertiesPerOwnerLimit", MaxPropertiesPerOwner),
                ("yamlBytesPerAssetLimit", MaxYamlBytes),
                ("candidateAssets", candidates.Count),
                ("scannedAssets", context.ScannedAssets),
                ("scannedSerializedOwners", context.ScannedOwners),
                ("count", context.Usages.Count),
                ("truncated", candidateSearchTruncated || context.ResultLimitReached ||
                              context.OwnerLimitReached || context.PropertyLimitReached ||
                              context.YamlLimitReached),
                ("truncationReasons", BuildTruncationReasons(candidateSearchTruncated, context)),
                ("usages", context.Usages),
                ("errors", context.Errors));
        }

        private static List<string> ResolveFolders(object value)
        {
            var values = new List<string>();
            if (value is string single)
            {
                values.Add(single);
            }
            else
            {
                foreach (var item in EvalData.AsArray(value) ?? new List<object?>())
                {
                    var text = Convert.ToString(item);
                    if (!string.IsNullOrWhiteSpace(text)) values.Add(text!);
                }
            }

            var folderInputs = values
                .Select(folder => folder.Replace('\\', '/').Trim().TrimEnd('/'))
                .Where(folder => folder.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (folderInputs.Count == 0)
                throw new InvalidOperationException(
                    "Argument 'folders' is required to prevent an unbounded project-wide scan.");

            var folders = new List<string>(folderInputs.Count);
            foreach (var folder in folderInputs)
            {
                if (!ToolUtilities.TryResolveProjectPath(folder, out _, out var projectPath, out var error))
                    throw new InvalidOperationException($"Invalid folder '{folder}': {error}");
                if (string.IsNullOrWhiteSpace(projectPath) || !AssetDatabase.IsValidFolder(projectPath))
                    throw new InvalidOperationException($"Asset folder '{folder}' was not found.");
                if (!IsAssetDatabasePath(projectPath))
                    throw new InvalidOperationException(
                        $"Folder '{folder}' must be under Assets or Packages.");
                if (!folders.Contains(projectPath, StringComparer.OrdinalIgnoreCase))
                    folders.Add(projectPath);
            }

            return folders;
        }

        private static bool IsAssetDatabasePath(string path)
        {
            return path.Equals("Assets", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("Packages", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> FindCandidatePaths(
            IReadOnlyList<string> folders,
            bool includeMemberAssets,
            int candidateLimit,
            out bool truncated,
            out List<object?> searchStats)
        {
            var filters = includeMemberAssets
                ? new[] { "t:Prefab", "t:Scene", "t:ScriptableObject", "t:AnimationClip" }
                : new[] { "t:Prefab", "t:Scene", "t:ScriptableObject" };
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            searchStats = new List<object?>(filters.Length);
            truncated = false;
            var baseQuota = candidateLimit / filters.Length;
            var remainder = candidateLimit % filters.Length;
            for (var index = 0; index < filters.Length; index++)
            {
                var retainedLimit = baseQuota + (index < remainder ? 1 : 0);
                var stats = AddCandidates(filters[index], folders, paths, retainedLimit, candidateLimit);
                searchStats.Add(stats);
                if (EvalData.GetBool(stats, "truncated")) truncated = true;
            }

            return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static Dictionary<string, object?> AddCandidates(
            string filter,
            IReadOnlyList<string> folders,
            ISet<string> paths,
            int retainedLimit,
            int totalLimit)
        {
            // AssetDatabase owns the returned GUID array, so its allocation cannot be
            // bounded here. Only one category array is live at a time; all paths retained
            // by this tool are bounded by both the category quota and total limit.
            var guids = AssetDatabase.FindAssets(filter, folders.ToArray());
            var processed = 0;
            var added = 0;
            var processedLimit = Math.Min(guids.Length, Math.Max(retainedLimit, retainedLimit * 2));
            for (var index = 0; index < guids.Length; index++)
            {
                if (processed >= processedLimit || added >= retainedLimit || paths.Count >= totalLimit) break;
                processed++;
                var path = AssetDatabase.GUIDToAssetPath(guids[index]);
                if (string.IsNullOrWhiteSpace(path) || path.EndsWith(".ldtk", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (paths.Add(path)) added++;
            }

            return EvalData.Obj(
                ("filter", filter),
                ("findAssetsCount", guids.Length),
                ("processedGuidCount", processed),
                ("processedGuidLimit", processedLimit),
                ("retainedPathCount", added),
                ("retainedPathLimit", retainedLimit),
                ("truncated", processed < guids.Length),
                ("note", "AssetDatabase.FindAssets allocates its GUID array before tool-side retention limits apply."));
        }

        private static void ScanAsset(string assetPath, SearchContext context)
        {
            context.ScannedAssets++;
            try
            {
                if (assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    var root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                    if (root != null) ScanHierarchy(root, assetPath, context);
                    return;
                }

                if (assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    ScanSceneYaml(assetPath, context);
                    return;
                }

                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                {
                    if (context.ShouldStop) break;
                    if (asset is ScriptableObject scriptableObject)
                        ScanScriptableObject(scriptableObject, assetPath, context);
                    if (context.HasMember && asset is AnimationClip clip)
                        ScanAnimationClip(clip, assetPath, context);
                }
            }
            catch (Exception exception)
            {
                context.AddError(assetPath, exception.Message);
            }
        }

        private static void ScanSceneYaml(string assetPath, SearchContext context)
        {
            if (!TryReadBoundedYaml(assetPath, context, out var lines))
                return;

            var gameObjectNames = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < lines.Length;)
            {
                if (!TryParseYamlHeader(lines[index], out var classId, out var fileId))
                {
                    index++;
                    continue;
                }

                var end = FindYamlBlockEnd(lines, index + 1);
                if (classId == 1)
                {
                    for (var lineIndex = index + 1; lineIndex < end; lineIndex++)
                    {
                        if (!TryReadYamlValue(lines[lineIndex], "m_Name", out var name)) continue;
                        gameObjectNames[fileId] = name;
                        break;
                    }
                }

                index = end;
            }

            for (var index = 0; index < lines.Length && !context.ShouldStop;)
            {
                if (!TryParseYamlHeader(lines[index], out var classId, out var fileId))
                {
                    index++;
                    continue;
                }

                var end = FindYamlBlockEnd(lines, index + 1);
                if (classId != 114)
                {
                    index = end;
                    continue;
                }

                var scriptMatches = false;
                var gameObjectFileId = string.Empty;
                for (var lineIndex = index + 1; lineIndex < end; lineIndex++)
                {
                    var trimmed = lines[lineIndex].Trim();
                    if (trimmed.StartsWith("m_Script:", StringComparison.Ordinal) &&
                        trimmed.IndexOf("guid: " + context.ScriptGuid, StringComparison.OrdinalIgnoreCase) >= 0)
                        scriptMatches = true;
                    if (TryReadYamlValue(lines[lineIndex], "m_GameObject", out var gameObjectValue))
                        gameObjectFileId = ReadInlineFileId(gameObjectValue);
                }

                if (!scriptMatches)
                {
                    index = end;
                    continue;
                }
                if (!context.TryVisitOwner())
                    return;

                var objectPath = gameObjectNames.TryGetValue(gameObjectFileId, out var gameObjectName)
                    ? $"{gameObjectName} (fileID {gameObjectFileId})"
                    : $"$yaml.fileID[{fileId}]";
                var componentName = context.ScriptType.FullName ?? context.ScriptType.Name;
                if (!context.HasMember)
                {
                    var usage = CreateUsage(
                        "monoBehaviourAttach", assetPath, objectPath, componentName,
                        "m_Script", string.Empty, "serializedGuidMatch");
                    usage["yamlFileId"] = fileId;
                    context.AddUsage(usage);
                    index = end;
                    continue;
                }

                var memberName = context.Member.Contains('.')
                    ? context.Member.Substring(context.Member.LastIndexOf('.') + 1)
                    : context.Member;
                for (var lineIndex = index + 1; lineIndex < end && !context.ShouldStop; lineIndex++)
                {
                    if (!TryReadYamlValue(lines[lineIndex], memberName, out var rawValue)) continue;
                    var usage = CreateUsage(
                        "serializedField", assetPath, objectPath, componentName,
                        $"$yaml.line[{lineIndex + 1}].{memberName}", context.Member,
                        "serializedGuidMatch");
                    usage["value"] = rawValue;
                    usage["serializedType"] = "Yaml";
                    usage["yamlFileId"] = fileId;
                    context.AddUsage(usage);
                    break;
                }

                index = end;
            }
        }

        private static int FindYamlBlockEnd(IReadOnlyList<string> lines, int start)
        {
            var index = start;
            while (index < lines.Count && !lines[index].StartsWith("--- !u!", StringComparison.Ordinal))
                index++;
            return index;
        }

        private static bool TryParseYamlHeader(string line, out int classId, out string fileId)
        {
            classId = 0;
            fileId = string.Empty;
            if (!line.StartsWith("--- !u!", StringComparison.Ordinal))
                return false;
            var ampersand = line.IndexOf('&');
            if (ampersand < 8 ||
                !int.TryParse(line.Substring(7, ampersand - 7).Trim(), out classId))
                return false;
            var end = line.IndexOf(' ', ampersand + 1);
            fileId = (end < 0 ? line.Substring(ampersand + 1) : line.Substring(ampersand + 1, end - ampersand - 1))
                .Trim();
            return fileId.Length > 0;
        }

        private static string ReadInlineFileId(string value)
        {
            var marker = "fileID:";
            var start = value.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return string.Empty;
            start += marker.Length;
            while (start < value.Length && char.IsWhiteSpace(value[start])) start++;
            var end = start;
            while (end < value.Length && (char.IsDigit(value[end]) || value[end] == '-')) end++;
            return value.Substring(start, end - start);
        }

        private static void ScanHierarchy(GameObject root, string assetPath, SearchContext context)
        {
            var stack = new Stack<GameObject>();
            stack.Push(root);
            while (stack.Count > 0 && !context.ShouldStop)
            {
                var gameObject = stack.Pop();
                var components = gameObject.GetComponents<Component>();
                foreach (var component in components)
                {
                    if (context.ShouldStop) break;
                    if (component == null) continue;
                    ScanComponent(component, assetPath, context);
                }

                for (var childIndex = gameObject.transform.childCount - 1; childIndex >= 0; childIndex--)
                    stack.Push(gameObject.transform.GetChild(childIndex).gameObject);
            }
        }

        private static void ScanComponent(Component component, string assetPath, SearchContext context)
        {
            if (!context.TryVisitOwner()) return;
            var objectPath = ToolUtilities.GetPath(component.gameObject);
            var componentName = component.GetType().FullName ?? component.GetType().Name;
            if (!context.HasMember)
            {
                if (!context.ScriptType.IsAssignableFrom(component.GetType())) return;
                context.AddUsage(CreateUsage(
                    "monoBehaviourAttach", assetPath, objectPath, componentName,
                    string.Empty, string.Empty, "resolved"));
                return;
            }

            var serialized = new SerializedObject(component);
            if (context.ScriptType.IsAssignableFrom(component.GetType()))
                ScanSerializedMember(serialized, assetPath, objectPath, componentName, context);
            ScanUnityEvents(serialized, assetPath, objectPath, componentName, context);
        }

        private static void ScanScriptableObject(
            ScriptableObject scriptableObject,
            string assetPath,
            SearchContext context)
        {
            if (!context.TryVisitOwner()) return;
            var objectPath = scriptableObject.name;
            var componentName = scriptableObject.GetType().FullName ?? scriptableObject.GetType().Name;
            if (!context.HasMember)
            {
                if (!context.ScriptType.IsAssignableFrom(scriptableObject.GetType())) return;
                context.AddUsage(CreateUsage(
                    "scriptableObjectAttach", assetPath, objectPath, componentName,
                    string.Empty, string.Empty, "resolved"));
                return;
            }

            var serialized = new SerializedObject(scriptableObject);
            if (context.ScriptType.IsAssignableFrom(scriptableObject.GetType()))
                ScanSerializedMember(serialized, assetPath, objectPath, componentName, context);
            ScanUnityEvents(serialized, assetPath, objectPath, componentName, context);
        }

        private static void ScanSerializedMember(
            SerializedObject serialized,
            string assetPath,
            string objectPath,
            string componentName,
            SearchContext context)
        {
            var iterator = serialized.GetIterator();
            var propertyCount = 0;
            while (!context.ShouldStop && propertyCount < MaxPropertiesPerOwner && iterator.Next(true))
            {
                propertyCount++;
                if (!PropertyMatchesMember(iterator, context.Member)) continue;
                var summary = SerializedTool.SummarizeProperty(iterator.Copy());
                summary.TryGetValue("value", out var value);
                var usage = CreateUsage(
                    "serializedField", assetPath, objectPath, componentName,
                    iterator.propertyPath, context.Member, "resolved");
                usage["value"] = value;
                usage["serializedType"] = iterator.propertyType.ToString();
                context.AddUsage(usage);
                return;
            }
            if (!context.ShouldStop && propertyCount >= MaxPropertiesPerOwner)
                context.PropertyLimitReached = true;
        }

        private static bool PropertyMatchesMember(SerializedProperty property, string member)
        {
            return string.Equals(property.propertyPath, member, StringComparison.Ordinal) ||
                   string.Equals(property.name, member, StringComparison.Ordinal);
        }

        private static void ScanUnityEvents(
            SerializedObject serialized,
            string assetPath,
            string ownerObjectPath,
            string ownerComponent,
            SearchContext context)
        {
            var iterator = serialized.GetIterator();
            var propertyCount = 0;
            while (!context.ShouldStop && propertyCount < MaxPropertiesPerOwner && iterator.Next(true))
            {
                propertyCount++;
                if (!iterator.propertyPath.EndsWith(".m_MethodName", StringComparison.Ordinal) ||
                    !string.Equals(iterator.stringValue, context.Member, StringComparison.Ordinal))
                    continue;

                var callPath = iterator.propertyPath.Substring(
                    0, iterator.propertyPath.Length - ".m_MethodName".Length);
                var targetProperty = serialized.FindProperty(callPath + ".m_Target");
                var targetTypeProperty = serialized.FindProperty(callPath + ".m_TargetAssemblyTypeName");
                var target = targetProperty?.objectReferenceValue;
                var serializedTargetInstanceId = targetProperty?.objectReferenceInstanceIDValue ?? 0;
                var targetTypeName = targetTypeProperty?.stringValue ?? string.Empty;
                var targetMatches = TargetMatchesScript(target, context.ScriptType) ||
                                    TypeNameMatches(targetTypeName, context.ScriptType);
                if (!targetMatches) continue;

                var status = target != null
                    ? "resolvedTarget"
                    : serializedTargetInstanceId != 0 ? "serializedTarget" : "missingTarget";
                var targetObjectPath = target != null
                    ? GetObjectPath(target)
                    : serializedTargetInstanceId != 0
                        ? $"$serialized.instanceID[{serializedTargetInstanceId}]"
                        : "<missing>";
                var targetComponent = target != null
                    ? target.GetType().FullName ?? target.GetType().Name
                    : context.ScriptType.FullName ?? context.ScriptType.Name;
                var usage = CreateUsage(
                    "unityEvent", assetPath, targetObjectPath, targetComponent,
                    iterator.propertyPath, context.Member, status);
                usage["ownerObjectPath"] = ownerObjectPath;
                usage["ownerComponent"] = ownerComponent;
                usage["targetAssemblyTypeName"] = targetTypeName;
                usage["targetInstanceId"] = serializedTargetInstanceId;
                usage["target"] = target != null ? SerializedTool.SummarizeObject(target) : null;
                context.AddUsage(usage);
                context.AddSemanticUnityEvent(
                    assetPath,
                    context.Member,
                    targetTypeName,
                    status == "missingTarget" ? "missingTarget" : "serializedTarget");
            }
            if (!context.ShouldStop && propertyCount >= MaxPropertiesPerOwner)
                context.PropertyLimitReached = true;
        }

        private static bool TargetMatchesScript(UnityEngine.Object? target, Type scriptType)
        {
            if (target == null) return false;
            if (scriptType.IsAssignableFrom(target.GetType())) return true;
            return target is GameObject gameObject && gameObject.GetComponent(scriptType) != null;
        }

        private static bool TypeNameMatches(string assemblyTypeName, Type scriptType)
        {
            if (string.IsNullOrWhiteSpace(assemblyTypeName)) return false;
            var typeName = assemblyTypeName.Split(',')[0].Trim();
            return string.Equals(typeName, scriptType.FullName, StringComparison.Ordinal) ||
                   string.Equals(typeName, scriptType.Name, StringComparison.Ordinal) ||
                   typeName.EndsWith("." + scriptType.Name, StringComparison.Ordinal);
        }

        private static string GetObjectPath(UnityEngine.Object target)
        {
            if (target is Component component) return ToolUtilities.GetPath(component.gameObject);
            if (target is GameObject gameObject) return ToolUtilities.GetPath(gameObject);
            return target.name;
        }

        private static void ScanAnimationClip(AnimationClip clip, string assetPath, SearchContext context)
        {
            foreach (var animationEvent in AnimationUtility.GetAnimationEvents(clip))
            {
                if (context.ShouldStop) return;
                if (!string.Equals(animationEvent.functionName, context.Member, StringComparison.Ordinal)) continue;
                var usage = CreateUsage(
                    "animationEvent", assetPath, clip.name,
                    "AnimationEvent",
                    "AnimationEvent.functionName", context.Member, "nameMatch");
                usage["targetScriptHint"] = context.ScriptType.FullName ?? context.ScriptType.Name;
                usage["matchScope"] = "functionNameOnly";
                usage["time"] = animationEvent.time;
                usage["floatParameter"] = animationEvent.floatParameter;
                usage["intParameter"] = animationEvent.intParameter;
                usage["stringParameter"] = animationEvent.stringParameter;
                context.AddUsage(usage);
            }
        }

        private static void ScanBrokenUnityEventYaml(string assetPath, SearchContext context)
        {
            if (!assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) &&
                !assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) &&
                !assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                return;
            if (!TryReadBoundedYaml(assetPath, context, out var lines))
                return;

            for (var index = 0; index < lines.Length && !context.ShouldStop; index++)
            {
                if (!TryReadYamlValue(lines[index], "m_MethodName", out var methodName) ||
                    !string.Equals(methodName, context.Member, StringComparison.Ordinal))
                    continue;

                var start = Math.Max(0, index - 8);
                var end = Math.Min(lines.Length - 1, index + 8);
                var targetTypeName = string.Empty;
                var targetValue = string.Empty;
                for (var nearby = start; nearby <= end; nearby++)
                {
                    if (TryReadYamlValue(lines[nearby], "m_Target", out var serializedTarget))
                        targetValue = serializedTarget;
                    if (TryReadYamlValue(lines[nearby], "m_TargetAssemblyTypeName", out var value))
                        targetTypeName = value;
                }

                if (!TypeNameMatches(targetTypeName, context.ScriptType)) continue;
                var targetFileId = ReadInlineFileId(targetValue);
                var status = string.IsNullOrWhiteSpace(targetFileId) || targetFileId == "0"
                    ? "missingTarget"
                    : "serializedTarget";
                if (context.TryConsumeSemanticUnityEvent(
                        assetPath,
                        context.Member,
                        targetTypeName,
                        status))
                    continue;
                var objectPath = status == "serializedTarget"
                    ? $"$yaml.fileID[{targetFileId}]"
                    : "<missing>";
                var usage = CreateUsage(
                    "unityEvent", assetPath, objectPath,
                    context.ScriptType.FullName ?? context.ScriptType.Name,
                    $"$yaml.line[{index + 1}].m_MethodName", context.Member,
                    status);
                usage["targetAssemblyTypeName"] = targetTypeName;
                usage["targetFileId"] = targetFileId;
                usage["serializedTarget"] = targetValue;
                usage["matchSource"] = "yamlTypeName";
                context.AddUsage(usage);
            }
        }

        private static bool TryReadBoundedYaml(string assetPath, SearchContext context, out string[] lines)
        {
            lines = Array.Empty<string>();
            if (!TryResolveAssetDiskPath(assetPath, out var fullPath) || !File.Exists(fullPath))
                return false;

            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > MaxYamlBytes)
            {
                context.YamlLimitReached = true;
                context.AddError(assetPath, $"YAML scan skipped because the file exceeds {MaxYamlBytes} bytes.");
                return false;
            }

            try
            {
                lines = File.ReadAllLines(fullPath);
                return true;
            }
            catch (Exception exception)
            {
                context.AddError(assetPath, "YAML scan failed: " + exception.Message);
                return false;
            }
        }

        private static bool TryResolveAssetDiskPath(string assetPath, out string fullPath)
        {
            fullPath = string.Empty;
            if (ToolUtilities.TryResolveProjectPath(assetPath, out var projectPath, out _, out _) &&
                File.Exists(projectPath))
            {
                fullPath = projectPath;
                return true;
            }

            if (!assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return false;
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            if (package == null || string.IsNullOrWhiteSpace(package.assetPath) ||
                string.IsNullOrWhiteSpace(package.resolvedPath))
                return false;
            var packageAssetRoot = package.assetPath.Replace('\\', '/').TrimEnd('/');
            if (!assetPath.Equals(packageAssetRoot, StringComparison.OrdinalIgnoreCase) &&
                !assetPath.StartsWith(packageAssetRoot + "/", StringComparison.OrdinalIgnoreCase))
                return false;

            var relative = assetPath.Substring(packageAssetRoot.Length).TrimStart('/');
            var resolvedRoot = Path.GetFullPath(package.resolvedPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(resolvedRoot, relative));
            var rootWithSeparator = resolvedRoot + Path.DirectorySeparatorChar;
            var pathComparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!candidate.Equals(resolvedRoot, pathComparison) &&
                !candidate.StartsWith(rootWithSeparator, pathComparison))
                return false;
            fullPath = candidate;
            return true;
        }

        private static bool TryReadYamlValue(string line, string key, out string value)
        {
            value = string.Empty;
            var trimmed = line.Trim();
            var prefix = key + ":";
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal)) return false;
            value = trimmed.Substring(prefix.Length).Trim().Trim('"', '\'');
            return true;
        }

        private static Dictionary<string, object?> CreateUsage(
            string kind,
            string assetPath,
            string objectPath,
            string component,
            string propertyPath,
            string member,
            string status)
        {
            return EvalData.Obj(
                ("kind", kind),
                ("assetPath", assetPath),
                ("objectPath", objectPath),
                ("component", component),
                ("propertyPath", propertyPath),
                ("member", member),
                ("status", status));
        }

        private static List<object?> BuildTruncationReasons(bool candidateSearchTruncated, SearchContext context)
        {
            var reasons = new List<object?>();
            if (candidateSearchTruncated) reasons.Add("candidateAssetLimit");
            if (context.OwnerLimitReached) reasons.Add("serializedOwnerLimit");
            if (context.PropertyLimitReached) reasons.Add("propertiesPerOwnerLimit");
            if (context.YamlLimitReached) reasons.Add("yamlBytesPerAssetLimit");
            if (context.ResultLimitReached) reasons.Add("resultLimit");
            return reasons;
        }

        private sealed class SearchContext
        {
            public SearchContext(
                string scriptPath,
                string scriptGuid,
                Type scriptType,
                string member,
                int resultLimit,
                int candidateLimit,
                int ownerLimit)
            {
                ScriptPath = scriptPath;
                ScriptGuid = scriptGuid;
                ScriptType = scriptType;
                Member = member;
                ResultLimit = resultLimit;
                CandidateLimit = candidateLimit;
                OwnerLimit = ownerLimit;
            }

            public string ScriptPath { get; }
            public string ScriptGuid { get; }
            public Type ScriptType { get; }
            public string Member { get; }
            public int ResultLimit { get; }
            public int CandidateLimit { get; }
            public int OwnerLimit { get; }
            public bool HasMember => Member.Length > 0;
            public int ScannedAssets { get; set; }
            public int ScannedOwners { get; private set; }
            public bool ResultLimitReached => Usages.Count >= ResultLimit;
            public bool OwnerLimitReached => ScannedOwners >= OwnerLimit;
            public bool PropertyLimitReached { get; set; }
            public bool YamlLimitReached { get; set; }
            public bool ShouldStop => ResultLimitReached || OwnerLimitReached;
            public List<object?> Usages { get; } = new();
            public List<object?> Errors { get; } = new();
            private Dictionary<string, int> SemanticUnityEventCounts { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            public void AddSemanticUnityEvent(
                string assetPath,
                string member,
                string targetTypeName,
                string targetStatus)
            {
                var key = GetSemanticUnityEventKey(assetPath, member, targetTypeName, targetStatus);
                SemanticUnityEventCounts.TryGetValue(key, out var count);
                SemanticUnityEventCounts[key] = count + 1;
            }

            public bool TryConsumeSemanticUnityEvent(
                string assetPath,
                string member,
                string targetTypeName,
                string targetStatus)
            {
                var key = GetSemanticUnityEventKey(assetPath, member, targetTypeName, targetStatus);
                if (!SemanticUnityEventCounts.TryGetValue(key, out var count) || count <= 0)
                    return false;
                if (count == 1) SemanticUnityEventCounts.Remove(key);
                else SemanticUnityEventCounts[key] = count - 1;
                return true;
            }

            private static string GetSemanticUnityEventKey(
                string assetPath,
                string member,
                string targetTypeName,
                string targetStatus) =>
                string.Join("\n", assetPath, member, targetTypeName, targetStatus);

            public bool TryVisitOwner()
            {
                if (OwnerLimitReached) return false;
                ScannedOwners++;
                return true;
            }

            public void AddUsage(Dictionary<string, object?> usage)
            {
                if (!ResultLimitReached) Usages.Add(usage);
            }

            public void AddError(string assetPath, string message)
            {
                if (Errors.Count >= 20) return;
                Errors.Add(EvalData.Obj(("assetPath", assetPath), ("message", message)));
            }
        }
    }
}
