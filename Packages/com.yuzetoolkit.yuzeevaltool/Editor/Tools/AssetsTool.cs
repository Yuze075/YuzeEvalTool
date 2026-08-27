#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace YuzeToolkit.Eval
{
    [EvalTool("Assets", "AssetDatabase search, text IO, asset moves, scripts, materials, dependencies, and script-safe refresh.")]
    public sealed partial class AssetsTool
    {
        [EvalFunction("AssetDatabase search returning asset summaries.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> find(string filter, int limit = 0, object? folders = null)
        {
            if (string.IsNullOrWhiteSpace(filter)) throw new InvalidOperationException("Argument 'filter' is required.");
            var guids = FindGuids(filter, folders, limit);
            var assets = guids.Select(guid => (object?)SummarizeAsset(AssetDatabase.GUIDToAssetPath(guid), guid)).ToList();
            return EvalData.Obj(("count", assets.Count), ("assets", assets));
        }

        [EvalFunction("AssetDatabase search returning asset paths.", Safety = EvalToolSafety.ReadOnly)]
        public List<string> findPaths(string filter, int limit = 0, object? folders = null)
        {
            if (string.IsNullOrWhiteSpace(filter)) throw new InvalidOperationException("Argument 'filter' is required.");
            return FindGuids(filter, folders, limit).Select(AssetDatabase.GUIDToAssetPath).ToList();
        }

        [EvalFunction("AssetDatabase search returning asset names.", Safety = EvalToolSafety.ReadOnly)]
        public List<string> findNames(string filter, int limit = 0, object? folders = null)
        {
            if (string.IsNullOrWhiteSpace(filter)) throw new InvalidOperationException("Argument 'filter' is required.");
            return FindGuids(filter, folders, limit).Select(guid =>
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                return asset != null ? asset.name : Path.GetFileNameWithoutExtension(path);
            }).ToList();
        }

        private static IEnumerable<string> FindGuids(string filter, object? folders, int limit)
        {
            var folderArray = ToStringArray(folders);
            var guids = folderArray.Length > 0 ? AssetDatabase.FindAssets(filter, folderArray) : AssetDatabase.FindAssets(filter);
            return limit > 0 ? guids.Take(limit) : guids.AsEnumerable();
        }

        internal static Dictionary<string, object?> SummarizeAsset(string path, string? guid = null)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            return EvalData.Obj(
                ("guid", string.IsNullOrEmpty(guid) ? AssetDatabase.AssetPathToGUID(path) : guid),
                ("path", path),
                ("name", asset != null ? asset.name : Path.GetFileNameWithoutExtension(path)),
                ("type", asset != null ? asset.GetType().FullName ?? asset.GetType().Name : string.Empty),
                ("exists", asset != null || File.Exists(path) || Directory.Exists(path)));
        }

        [EvalFunction("Get asset info.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> getInfo(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Argument 'path' is required.");
            return SummarizeAsset(path);
        }

        [EvalFunction("Read a project text asset.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> readText(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Argument 'path' is required.");
            if (!ToolUtilities.TryResolveProjectPath(path, out var fullPath, out var projectPath, out var error))
                throw new InvalidOperationException(error);
            if (!File.Exists(fullPath)) throw new InvalidOperationException($"File '{projectPath}' was not found.");
            return EvalData.Obj(("path", projectPath), ("text", File.ReadAllText(fullPath)));
        }

        [EvalFunction("Write a project text asset.", Safety = EvalToolSafety.MutatesProject)]
        public Dictionary<string, object?> writeText(string path, string text, bool refresh = false, bool confirmOverwrite = false)
        {
            if (!ToolUtilities.TryResolveProjectPath(path, out var full, out var projectPath, out var error))
                throw new InvalidOperationException(error);
            if (File.Exists(full) && !confirmOverwrite)
                throw new InvalidOperationException("Writing an existing file requires confirmOverwrite: true.");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text ?? string.Empty);
            if (refresh) EditorCompilationMonitor.RefreshAssetDatabaseNow();
            return SummarizeAsset(projectPath);
        }

        [EvalFunction("Create a folder.", Safety = EvalToolSafety.MutatesProject)]
        public Dictionary<string, object?> createFolder(
            [EvalParameter("Required new folder name to create under the parent project folder.")]
            string name,
            [EvalParameter("Parent project folder path, such as Assets or Packages/com.example.")]
            string parent = "Assets")
        {
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Argument 'name' is required.");
            var guid = AssetDatabase.CreateFolder(parent, name);
            return EvalData.Obj(("guid", guid), ("path", AssetDatabase.GUIDToAssetPath(guid)));
        }

        [EvalFunction("Move an asset.", Safety = EvalToolSafety.MutatesProject | EvalToolSafety.RequiresConfirmation)]
        public Dictionary<string, object?> move(string from, string to, bool confirm = false)
        {
            if (!confirm) throw new InvalidOperationException("Asset move requires confirm: true.");
            var error = AssetDatabase.MoveAsset(from, to);
            if (error.Length != 0) throw new InvalidOperationException(error);
            return SummarizeAsset(to);
        }

        [EvalFunction("Copy an asset.", Safety = EvalToolSafety.MutatesProject)]
        public Dictionary<string, object?> copy(string from, string to, bool confirmOverwrite = false)
        {
            if (string.Equals(NormalizeAssetPath(from), NormalizeAssetPath(to), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Source and destination asset paths must be different.");
            if (AssetDatabase.LoadMainAssetAtPath(to) != null && !confirmOverwrite)
                throw new InvalidOperationException("Copying over an existing asset requires confirmOverwrite: true.");
            if (AssetDatabase.LoadMainAssetAtPath(to) != null && confirmOverwrite && !AssetDatabase.DeleteAsset(to))
                throw new InvalidOperationException($"Failed to delete existing destination asset '{to}'.");
            var ok = AssetDatabase.CopyAsset(from, to);
            if (!ok) throw new InvalidOperationException($"Failed to copy '{from}' to '{to}'.");
            return SummarizeAsset(to);
        }

        private static string NormalizeAssetPath(string path) =>
            (path ?? string.Empty).Replace('\\', '/').Trim();

        [EvalFunction("Delete an asset.", Safety = EvalToolSafety.MutatesProject | EvalToolSafety.Destructive | EvalToolSafety.RequiresConfirmation)]
        public Dictionary<string, object?> deleteAsset(string path, bool confirm = false)
        {
            if (!confirm) throw new InvalidOperationException("Asset delete requires confirm: true.");
            var ok = AssetDatabase.DeleteAsset(path);
            if (!ok) throw new InvalidOperationException($"Failed to delete asset '{path}'.");
            return EvalData.Obj(("deleted", path));
        }

        [EvalFunction("Request AssetDatabase refresh for script changes and return compilation/import state. If Unity is playing or changing play mode, the request exits PlayMode first and runs after stable EditMode.", Safety = EvalToolSafety.MutatesProject | EvalToolSafety.TriggersReload)]
        public Dictionary<string, object?> refreshNow()
        {
            return EditorCompilationMonitor.RefreshAssetDatabaseNow();
        }

        [EvalFunction("Get asset dependencies.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> getDependencies(string path, bool recursive = true)
        {
            var deps = AssetDatabase.GetDependencies(path, recursive).Select(p => (object?)SummarizeAsset(p)).ToList();
            return EvalData.Obj(("path", path), ("count", deps.Count), ("dependencies", deps));
        }

        [EvalFunction("Find asset references.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> findReferences(string path, [EvalParameter("Required folder path or array of folder paths that limits the search scope.")] object folders, int limit = 0)
        {
            var scope = ToStringArray(folders);
            if (scope.Length == 0)
                throw new InvalidOperationException("Argument 'folders' is required for findReferences to avoid scanning the whole project.");
            limit = limit <= 0 ? 100 : Math.Max(1, limit);
            var results = new List<object?>();
            foreach (var guid in AssetDatabase.FindAssets("", scope))
            {
                var candidate = AssetDatabase.GUIDToAssetPath(guid);
                if (candidate == path) continue;
                if (AssetDatabase.GetDependencies(candidate, true).Contains(path))
                    results.Add(SummarizeAsset(candidate, guid));
                if (results.Count >= limit) break;
            }
            return EvalData.Obj(("target", path), ("count", results.Count), ("references", results));
        }

        [EvalFunction("Create a MonoBehaviour script.", Safety = EvalToolSafety.MutatesProject | EvalToolSafety.TriggersReload)]
        public Dictionary<string, object?> createScript(string path, string className = "", string namespaceName = "", bool confirmOverwrite = false)
        {
            if (!ToolUtilities.TryResolveProjectPath(path, out var fullPath, out var projectPath, out var error))
                throw new InvalidOperationException(error);
            if (File.Exists(fullPath) && !confirmOverwrite)
                throw new InvalidOperationException("Creating over an existing script requires confirmOverwrite: true.");
            className = string.IsNullOrWhiteSpace(className) ? Path.GetFileNameWithoutExtension(fullPath) : className;
            var body = string.IsNullOrWhiteSpace(namespaceName)
                ? $"using UnityEngine;\n\npublic class {className} : MonoBehaviour\n{{\n}}\n"
                : $"using UnityEngine;\n\nnamespace {namespaceName}\n{{\n    public class {className} : MonoBehaviour\n    {{\n    }}\n}}\n";
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, body);
            EditorCompilationMonitor.RefreshAssetDatabaseNow();
            return SummarizeAsset(projectPath);
        }

        [EvalFunction("Apply character-based script text edits.", Safety = EvalToolSafety.MutatesProject | EvalToolSafety.RequiresConfirmation | EvalToolSafety.TriggersReload)]
        public Dictionary<string, object?> applyScriptTextEdits(string path, object edits, bool refresh = true, bool confirm = false)
        {
            if (!confirm) throw new InvalidOperationException("Script text edits require confirm: true.");
            if (!ToolUtilities.TryResolveProjectPath(path, out var fullPath, out var projectPath, out var error))
                throw new InvalidOperationException(error);
            if (!File.Exists(fullPath)) throw new InvalidOperationException($"File '{projectPath}' was not found.");
            var text = File.ReadAllText(fullPath);
            var editList = (EvalData.AsArray(edits) ?? new List<object?>())
                .Select(EvalData.AsObject)
                .Where(edit => edit != null)
                .Select(edit => (Start: EvalData.GetInt(edit!, "start", -1), Length: EvalData.GetInt(edit!, "length", 0), Text: ToolUtilities.GetString(edit!, "text")))
                .OrderByDescending(edit => edit.Start)
                .ToList();
            foreach (var edit in editList)
            {
                if (edit.Start < 0 || edit.Start > text.Length) throw new InvalidOperationException($"Invalid edit start {edit.Start}.");
                if (edit.Length < 0) throw new InvalidOperationException($"Invalid edit length {edit.Length}.");
                text = text.Remove(edit.Start, Math.Min(edit.Length, text.Length - edit.Start)).Insert(edit.Start, edit.Text);
            }
            File.WriteAllText(fullPath, text);
            if (refresh) EditorCompilationMonitor.RefreshAssetDatabaseNow();
            return EvalData.Obj(("path", projectPath), ("editCount", editList.Count));
        }

        [EvalFunction("Create a Material asset.", Safety = EvalToolSafety.MutatesProject)]
        public Dictionary<string, object?> createMaterial(string path, string shaderName = "Universal Render Pipeline/2D/Sprite-Lit-Default", object? properties = null, bool confirmOverwrite = false)
        {
            if (!ToolUtilities.TryResolveProjectPath(path, out _, out var projectPath, out var pathError))
                throw new InvalidOperationException(pathError);
            if (!string.Equals(Path.GetExtension(projectPath), ".mat", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Material asset path must end with .mat.");
            var parentPath = Path.GetDirectoryName(projectPath)?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(parentPath) || !AssetDatabase.IsValidFolder(parentPath))
                throw new InvalidOperationException($"Material parent folder '{parentPath}' was not found.");
            var shader = Shader.Find(shaderName) ?? Shader.Find("Sprites/Default");
            if (shader == null) throw new InvalidOperationException($"Shader '{shaderName}' was not found.");
            if (AssetDatabase.LoadMainAssetAtPath(projectPath) != null)
            {
                if (!confirmOverwrite)
                    throw new InvalidOperationException("Creating over an existing material requires confirmOverwrite: true.");
                if (!AssetDatabase.DeleteAsset(projectPath))
                    throw new InvalidOperationException($"Failed to delete existing material '{projectPath}'.");
            }
            var material = new Material(shader);
            ApplyMaterialProperties(material, properties);
            AssetDatabase.CreateAsset(material, projectPath);
            AssetDatabase.SaveAssets();
            return SummarizeAsset(projectPath);
        }

        private static void ApplyMaterialProperties(Material material, object? properties)
        {
            var map = EvalData.AsObject(properties);
            if (map == null) return;
            foreach (var pair in map)
            {
                if (!material.HasProperty(pair.Key)) continue;
                if (pair.Value is int or long or float or double)
                    material.SetFloat(pair.Key, ToolUtilities.ToFloat(pair.Value, 0f));
                else if (EvalData.AsObject(pair.Value) is { } color)
                    material.SetColor(pair.Key, new Color(
                        EvalData.GetFloat(color, "r", 1f),
                        EvalData.GetFloat(color, "g", 1f),
                        EvalData.GetFloat(color, "b", 1f),
                        EvalData.GetFloat(color, "a", 1f)));
                else if (pair.Value is string assetPath)
                {
                    var texture = AssetDatabase.LoadAssetAtPath<Texture>(assetPath);
                    if (texture != null) material.SetTexture(pair.Key, texture);
                }
            }
        }

        private static string[] ToStringArray(object? value)
        {
            if (value == null) return Array.Empty<string>();
            if (value is string single) return string.IsNullOrWhiteSpace(single) ? Array.Empty<string>() : new[] { single };
            return (EvalData.AsArray(value) ?? new List<object?>())
                .Select(Convert.ToString)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text!)
                .ToArray();
        }
    }
}
