#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace YuzeToolkit.Agent
{
    /// <summary>
    /// Loads provider-free defaults from the package JSON, optionally overridden by the project
    /// Resources JSON. Configuration values are never duplicated as C# defaults.
    /// </summary>
    public static class UnityAgentProjectSettings
    {
        public const string ResourceName = "UnityAgentProjectSettings";
        public const string PackageResourceName = "UnityAgentPackageSettings";

        public static AgentProjectSettingsDocument Load()
        {
            var packageDefaults = LoadPackageDefaults();
            var asset = Resources.Load<TextAsset>(ResourceName);
            if (asset == null) return packageDefaults;
            try
            {
                var settings = AgentDocumentCodec.DeserializeProjectSettings(asset.text, packageDefaults);
                Validate(settings);
                return settings;
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or
                                               InvalidOperationException or OverflowException)
            {
                throw new InvalidDataException(
                    $"Project Resources/{ResourceName}.json is invalid. Correct or remove the project override.",
                    exception);
            }
        }

        public static AgentProjectSettingsDocument LoadPackageDefaults()
        {
            var asset = Resources.Load<TextAsset>(PackageResourceName);
            if (asset == null)
                throw new InvalidOperationException(
                    $"UnityAgentTool package default Resources/{PackageResourceName}.json is missing.");
            try
            {
                var settings = AgentDocumentCodec.DeserializeProjectSettings(asset.text);
                Validate(settings);
                return settings;
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or
                                               InvalidOperationException or OverflowException)
            {
                throw new InvalidDataException(
                    $"UnityAgentTool package default Resources/{PackageResourceName}.json is invalid.",
                    exception);
            }
        }

        public static AgentSettingsDocument CreateMachineDefaults()
        {
            return AgentSettingsDocument.CreateDefault(Load());
        }

        /// <summary>Parses and validates one provider-free project settings document.</summary>
        public static AgentProjectSettingsDocument Deserialize(
            string json,
            AgentProjectSettingsDocument? packageDefaults = null)
        {
            var settings = AgentDocumentCodec.DeserializeProjectSettings(json, packageDefaults);
            Validate(settings);
            return settings;
        }

        /// <summary>Validates and serializes one provider-free project settings document.</summary>
        public static string Serialize(AgentProjectSettingsDocument settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            Validate(settings);
            return AgentDocumentCodec.SerializeProjectSettings(settings);
        }

        public static string Serialize(AgentSettingsDocument settings) =>
            AgentDocumentCodec.SerializeProjectSettings(AgentProjectSettingsDocument.FromSettings(settings));

        private static void Validate(AgentProjectSettingsDocument settings)
        {
            if (!Enum.IsDefined(typeof(AgentPermissionMode), settings.PermissionMode))
                throw new FormatException("Project settings contain an unknown Agent permission mode.");
            if (string.IsNullOrWhiteSpace(settings.EditorSystemPrompt) ||
                string.IsNullOrWhiteSpace(settings.RuntimeSystemPrompt))
                throw new FormatException("Editor and Runtime system prompts are required.");
            if (settings.DefaultToolTimeoutSeconds < 1)
                throw new FormatException("Default Tool timeout must be positive.");
            if (settings.MaximumAgentSteps < 1)
                throw new FormatException("Maximum Agent steps must be positive.");
            ValidateRoots(settings.AgentsRoots, "AGENTS.md");
            ValidateRoots(settings.SkillRoots, "Skill");
        }

        private static void ValidateRoots(IReadOnlyList<AgentPathLocation> roots, string name)
        {
            if (roots == null || roots.Count == 0)
                throw new FormatException($"{name} roots are required.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var root in roots)
            {
                AgentPaths.Validate(root, name + " root");
                if (!ids.Add(root.Id))
                    throw new FormatException($"Duplicate {name} root id '{root.Id}'.");
            }
        }
    }
}
