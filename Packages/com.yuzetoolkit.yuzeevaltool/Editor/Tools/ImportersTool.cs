#nullable enable
using System.Collections.Generic;
using UnityEditor;

namespace YuzeToolkit.Eval
{
    [EvalTool("Importers", "AssetImporter inspection, serialized edits, and reimport.")]
    public sealed partial class ImportersTool
    {
        [EvalFunction("Read importer summary.", Safety = EvalToolSafety.ReadOnly)]
        public Dictionary<string, object?> get(string path, bool includeProperties = false, int propertyLimit = 200)
        {
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null) throw new System.InvalidOperationException($"Importer for '{path}' was not found.");
            return SummarizeImporter(importer, includeProperties, propertyLimit);
        }

        private static Dictionary<string, object?> SummarizeImporter(AssetImporter importer, bool includeProperties, int propertyLimit = 200)
        {
            var result = EvalData.Obj(
                ("assetPath", importer.assetPath),
                ("type", importer.GetType().FullName ?? importer.GetType().Name),
                ("userData", importer.userData),
                ("assetBundleName", importer.assetBundleName),
                ("assetBundleVariant", importer.assetBundleVariant)
            );

            if (importer is TextureImporter texture)
            {
                result["texture"] = EvalData.Obj(
                    ("textureType", texture.textureType.ToString()),
                    ("spriteImportMode", texture.spriteImportMode.ToString()),
                    ("spritePixelsPerUnit", texture.spritePixelsPerUnit),
                    ("mipmapEnabled", texture.mipmapEnabled),
                    ("alphaIsTransparency", texture.alphaIsTransparency),
                    ("isReadable", texture.isReadable),
                    ("maxTextureSize", texture.maxTextureSize),
                    ("textureCompression", texture.textureCompression.ToString()),
                    ("filterMode", texture.filterMode.ToString())
                );
            }
            else if (importer is AudioImporter audio)
            {
                var settings = audio.defaultSampleSettings;
                result["audio"] = EvalData.Obj(
                    ("loadType", settings.loadType.ToString()),
                    ("compressionFormat", settings.compressionFormat.ToString()),
                    ("quality", settings.quality),
                    ("sampleRateSetting", settings.sampleRateSetting.ToString())
                );
            }
            else if (importer is ModelImporter model)
            {
                result["model"] = EvalData.Obj(
                    ("importAnimation", model.importAnimation),
                    ("importCameras", model.importCameras),
                    ("importLights", model.importLights),
                    ("globalScale", model.globalScale)
                );
            }

            if (includeProperties)
            {
                propertyLimit = System.Math.Max(1, propertyLimit);
                var serialized = new SerializedObject(importer);
                var props = new List<object?>();
                var iterator = serialized.GetIterator();
                var enterChildren = true;
                while (props.Count < propertyLimit && iterator.NextVisible(enterChildren))
                {
                    enterChildren = false;
                    props.Add(SerializedTool.SummarizeProperty(iterator));
                }
                result["properties"] = props;
                result["propertyCount"] = props.Count;
                result["propertyLimit"] = propertyLimit;
            }

            return result;
        }

        [EvalFunction("Set one importer property.", Safety = EvalToolSafety.MutatesProject | EvalToolSafety.RequiresConfirmation)]
        public Dictionary<string, object?> setProperty(string path, string propertyPath, object? value, bool saveAndReimport = false, bool confirm = false)
        {
            if (!confirm) throw new System.InvalidOperationException("Importer property edits require confirm: true.");
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null) throw new System.InvalidOperationException($"Importer for '{path}' was not found.");
            var serialized = new SerializedObject(importer);
            var prop = serialized.FindProperty(propertyPath);
            if (prop == null) throw new System.InvalidOperationException($"Importer property '{propertyPath}' was not found.");
            SerializedTool.SetPropertyValue(prop, value);
            serialized.ApplyModifiedProperties();
            if (saveAndReimport) importer.SaveAndReimport();
            else EditorUtility.SetDirty(importer);
            return SummarizeImporter(importer, false);
        }

        [EvalFunction("Set multiple importer properties from an array of {propertyPath,value} or an object map of propertyPath -> value.", Safety = EvalToolSafety.MutatesProject | EvalToolSafety.RequiresConfirmation)]
        public Dictionary<string, object?> setMany(string path, object changes, bool saveAndReimport = false, bool confirm = false)
        {
            if (!confirm) throw new System.InvalidOperationException("Importer property edits require confirm: true.");
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null) throw new System.InvalidOperationException($"Importer for '{path}' was not found.");
            var changeList = ToolUtilities.GetPropertyChanges(changes);
            if (changeList.Count == 0) throw new System.InvalidOperationException("Argument 'changes' must contain at least one importer property update.");

            var serialized = new SerializedObject(importer);
            var results = new List<object?>();
            foreach (var change in changeList)
            {
                var propertyPath = ToolUtilities.GetString(change, "propertyPath");
                var prop = serialized.FindProperty(propertyPath);
                if (prop == null) throw new System.InvalidOperationException($"Importer property '{propertyPath}' was not found.");
                change.TryGetValue("value", out var value);
                SerializedTool.SetPropertyValue(prop, value);
                results.Add(SerializedTool.SummarizeProperty(prop));
            }

            serialized.ApplyModifiedProperties();
            if (saveAndReimport) importer.SaveAndReimport();
            else EditorUtility.SetDirty(importer);
            return EvalData.Obj(("importer", SummarizeImporter(importer, false)), ("count", results.Count), ("properties", results));
        }

        [EvalFunction("Force reimport.", Safety = EvalToolSafety.MutatesProject | EvalToolSafety.RequiresConfirmation | EvalToolSafety.TriggersReload)]
        public Dictionary<string, object?> reimport(string path, bool confirm = false)
        {
            if (!confirm) throw new System.InvalidOperationException("Asset reimport requires confirm: true.");
            if (string.IsNullOrWhiteSpace(path)) throw new System.InvalidOperationException("Argument 'path' is required.");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null) throw new System.InvalidOperationException($"Importer for '{path}' was not found after reimport.");
            return SummarizeImporter(importer, false);
        }
    }
}
