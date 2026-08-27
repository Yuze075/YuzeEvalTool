#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace YuzeToolkit.UnityAgent
{
    internal static class AgentDocumentCodec
    {
        private const string LegacyCodexAppServerProtocol = "codex-app-server";

        public static string SerializeSettings(AgentSettingsDocument settings) =>
            AgentJson.Stringify(ToJson(settings));

        public static string SerializeMachineSettings(AgentSettingsDocument settings) =>
            AgentJson.Stringify(ToMachineJson(settings));

        public static string SerializeProviderSettings(AgentSettingsDocument settings) =>
            AgentJson.Stringify(ToProviderJson(AgentProviderSettingsDocument.FromSettings(settings)));

        public static AgentSettingsDocument DeserializeMachineSettings(
            string json,
            AgentProjectSettingsDocument? projectDefaults = null)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new FormatException("Agent machine settings JSON is empty.");
            var root = AgentJson.ParseObject(json);
            var sourceSchemaVersion = AgentJson.GetSchemaVersion(root);
            if (sourceSchemaVersion != AgentSettingsDocument.CurrentSchemaVersion)
            {
                throw new FormatException(
                    $"Settings schema version {sourceSchemaVersion} is not supported; expected " +
                    $"{AgentSettingsDocument.CurrentSchemaVersion}.");
            }
            return ReadMachineSettings(root, projectDefaults);
        }

        public static AgentProviderSettingsDocument DeserializeProviderSettings(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new FormatException("Agent Provider settings JSON is empty.");
            var root = AgentJson.ParseObject(json);
            var sourceSchemaVersion = AgentJson.GetSchemaVersion(root);
            if (sourceSchemaVersion != AgentProviderSettingsDocument.CurrentSchemaVersion)
            {
                throw new FormatException(
                    $"Provider settings schema version {sourceSchemaVersion} is not supported; expected " +
                    $"{AgentProviderSettingsDocument.CurrentSchemaVersion}.");
            }
            return ReadProviderSettings(root);
        }

        public static AgentSettingsDocument DeserializeSettings(
            string json,
            AgentProjectSettingsDocument? projectDefaults = null)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new FormatException("Agent settings JSON is empty.");
            var root = AgentJson.ParseObject(json);
            var sourceSchemaVersion = AgentJson.GetSchemaVersion(root);
            if (sourceSchemaVersion != AgentSettingsDocument.CurrentSchemaVersion)
            {
                throw new FormatException(
                    $"Settings schema version {sourceSchemaVersion} is not supported; expected " +
                    $"{AgentSettingsDocument.CurrentSchemaVersion}.");
            }
            var settings = new AgentSettingsDocument
            {
                SchemaVersion = AgentSettingsDocument.CurrentSchemaVersion,
                DefaultProviderProfileId = AgentJson.GetString(root, "defaultProviderProfileId"),
                PermissionMode = ReadRequiredEnum(root, "permissionMode", projectDefaults?.PermissionMode),
                EditorSystemPrompt = ReadRequiredString(root, "editorSystemPrompt", projectDefaults?.EditorSystemPrompt),
                RuntimeSystemPrompt = ReadRequiredString(root, "runtimeSystemPrompt", projectDefaults?.RuntimeSystemPrompt),
                DefaultToolTimeoutSeconds = ReadRequiredInt(root, "defaultToolTimeoutSeconds",
                    projectDefaults?.DefaultToolTimeoutSeconds),
                MaximumAgentSteps = ReadRequiredInt(root, "maximumAgentSteps", projectDefaults?.MaximumAgentSteps)
            };

            foreach (var value in AgentJson.GetObjectArray(root, "providerProfiles"))
                settings.ProviderProfiles.Add(ReadProviderProfile(value));

            foreach (var value in AgentJson.GetObjectArray(root, "agentsRoots"))
                settings.AgentsRoots.Add(ReadPathLocation(value));
            foreach (var value in AgentJson.GetObjectArray(root, "skillRoots"))
                settings.SkillRoots.Add(ReadPathLocation(value));

            if (settings.ProviderProfiles.Count == 0)
            {
                var defaultProfile = new AgentProviderProfile();
                if (!AgentProviderCatalog.ApplyPreset(defaultProfile, "openai"))
                    throw new InvalidOperationException("The built-in OpenAI Provider preset is missing.");
                settings.ProviderProfiles.Add(defaultProfile);
                settings.DefaultProviderProfileId = defaultProfile.Id;
            }
            if (string.IsNullOrWhiteSpace(settings.DefaultProviderProfileId) ||
                settings.ProviderProfiles.All(profile => profile.Id != settings.DefaultProviderProfileId))
                settings.DefaultProviderProfileId = settings.ProviderProfiles[0].Id;
            if (string.IsNullOrWhiteSpace(settings.EditorSystemPrompt) ||
                string.IsNullOrWhiteSpace(settings.RuntimeSystemPrompt))
                throw new FormatException("Editor and Runtime system prompts are required.");
            if (settings.DefaultToolTimeoutSeconds < 1)
                throw new FormatException("Default Tool timeout must be positive.");
            if (settings.MaximumAgentSteps < 1)
                throw new FormatException("Maximum Agent steps must be positive.");
            settings.SchemaVersion = AgentSettingsDocument.CurrentSchemaVersion;
            return settings;
        }

        private static AgentSettingsDocument ReadMachineSettings(
            Dictionary<string, object?> root,
            AgentProjectSettingsDocument? projectDefaults)
        {
            var settings = new AgentSettingsDocument
            {
                SchemaVersion = AgentSettingsDocument.CurrentSchemaVersion,
                PermissionMode = ReadRequiredEnum(root, "permissionMode", projectDefaults?.PermissionMode),
                EditorSystemPrompt = ReadRequiredString(root, "editorSystemPrompt", projectDefaults?.EditorSystemPrompt),
                RuntimeSystemPrompt = ReadRequiredString(root, "runtimeSystemPrompt", projectDefaults?.RuntimeSystemPrompt),
                DefaultToolTimeoutSeconds = ReadRequiredInt(root, "defaultToolTimeoutSeconds",
                    projectDefaults?.DefaultToolTimeoutSeconds),
                MaximumAgentSteps = ReadRequiredInt(root, "maximumAgentSteps", projectDefaults?.MaximumAgentSteps)
            };

            foreach (var value in AgentJson.GetObjectArray(root, "agentsRoots"))
                settings.AgentsRoots.Add(ReadPathLocation(value));
            foreach (var value in AgentJson.GetObjectArray(root, "skillRoots"))
                settings.SkillRoots.Add(ReadPathLocation(value));

            if (string.IsNullOrWhiteSpace(settings.EditorSystemPrompt) ||
                string.IsNullOrWhiteSpace(settings.RuntimeSystemPrompt))
                throw new FormatException("Editor and Runtime system prompts are required.");
            if (settings.DefaultToolTimeoutSeconds < 1)
                throw new FormatException("Default Tool timeout must be positive.");
            if (settings.MaximumAgentSteps < 1)
                throw new FormatException("Maximum Agent steps must be positive.");
            return settings;
        }

        private static AgentProviderSettingsDocument ReadProviderSettings(Dictionary<string, object?> root)
        {
            var result = new AgentProviderSettingsDocument
            {
                SchemaVersion = AgentProviderSettingsDocument.CurrentSchemaVersion,
                DefaultProviderProfileId = AgentJson.GetString(root, "defaultProviderProfileId")
            };
            foreach (var value in AgentJson.GetObjectArray(root, "providerProfiles"))
                result.ProviderProfiles.Add(ReadProviderProfile(value));

            if (result.ProviderProfiles.Count == 0)
                throw new FormatException("Provider settings require at least one Provider profile.");
            if (string.IsNullOrWhiteSpace(result.DefaultProviderProfileId) ||
                result.ProviderProfiles.All(profile => profile.Id != result.DefaultProviderProfileId))
                result.DefaultProviderProfileId = result.ProviderProfiles[0].Id;
            return result;
        }

        public static string SerializeSession(AgentSessionDocument session) =>
            AgentJson.Stringify(ToJson(session));

        public static AgentSessionDocument DeserializeSession(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new FormatException("Agent session JSON is empty.");
            var root = AgentJson.ParseObject(json);
            var sourceSchemaVersion = AgentJson.GetSchemaVersion(root);
            if (sourceSchemaVersion > AgentSessionDocument.CurrentSchemaVersion)
            {
                throw new FormatException(
                    $"Session schema version {sourceSchemaVersion} is newer than the supported version " +
                    $"{AgentSessionDocument.CurrentSchemaVersion}.");
            }
            var sessionId = AgentJson.GetString(root, "id");
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new FormatException("Agent session JSON property 'id' is required.");
            var session = new AgentSessionDocument
            {
                SchemaVersion = sourceSchemaVersion,
                Id = sessionId,
                Title = AgentJson.GetString(root, "title", "New conversation"),
                CreatedAtUtc = AgentJson.GetDateTime(root, "createdAtUtc", DateTime.UtcNow),
                UpdatedAtUtc = AgentJson.GetDateTime(root, "updatedAtUtc", DateTime.UtcNow),
                ProviderProfileId = AgentJson.GetString(root, "providerProfileId"),
                Model = AgentJson.GetString(root, "model"),
                ReasoningEffort = AgentJson.GetString(root, "reasoningEffort"),
                PermissionMode = AgentJson.GetEnum(root, "permissionMode", AgentPermissionMode.ObserveOnly),
                SystemPrompt = AgentJson.GetString(root, "systemPrompt"),
                WorkingDirectory = AgentJson.GetString(root, "workingDirectory"),
                ProviderThreadId = AgentJson.GetString(root, "providerThreadId"),
                State = AgentJson.GetEnum(root, "state", AgentSessionState.Idle),
                Summary = AgentJson.GetString(root, "summary"),
                SummarizedMessageCount = Math.Max(0, EvalData.GetInt(root, "summarizedMessageCount")),
                ContextSummaryMessageCount = Math.Max(0,
                    EvalData.GetInt(root, "contextSummaryMessageCount")),
                CompletedSteps = Math.Max(0, EvalData.GetInt(root, "completedSteps")),
                LastError = AgentJson.GetString(root, "lastError"),
                IsPinned = EvalData.GetBool(root, "isPinned"),
                IsArchived = EvalData.GetBool(root, "isArchived"),
                SortOrder = EvalData.GetInt(root, "sortOrder"),
                Draft = AgentJson.GetString(root, "draft")
            };

            foreach (var value in AgentJson.GetObjectArray(root, "messages"))
                session.Messages.Add(ReadMessage(value));
            session.ContextSummaryMessageCount = Math.Min(session.ContextSummaryMessageCount,
                session.Messages.Count);

            // Schema V1/V2 retained both the summarized prefix and the summary. V3 makes the
            // summary authoritative and physically removes that prefix so histories remain bounded.
            // This exactly preserves the old ProjectMessages projection (summary + unsummarized tail).
            if (sourceSchemaVersion < 3 && session.SummarizedMessageCount > 0 &&
                !string.IsNullOrWhiteSpace(session.Summary))
            {
                var summarizedPrefix = Math.Min(session.SummarizedMessageCount, session.Messages.Count);
                if (summarizedPrefix > 0) session.Messages.RemoveRange(0, summarizedPrefix);
            }

            if (AgentJson.GetOptionalObject(root, "usage") is { } usage)
            {
                session.Usage.InputTokens = AgentJson.GetLong(usage, "inputTokens");
                session.Usage.OutputTokens = AgentJson.GetLong(usage, "outputTokens");
            }

            if (AgentJson.GetOptionalObject(root, "pendingApproval") is { } approval)
                session.PendingApproval = ReadApproval(approval);
            session.SchemaVersion = AgentSessionDocument.CurrentSchemaVersion;
            return session;
        }

        public static AgentSessionDocument Clone(AgentSessionDocument session) =>
            DeserializeSession(SerializeSession(session));

        public static AgentSettingsDocument Clone(AgentSettingsDocument settings) =>
            DeserializeSettings(SerializeSettings(settings));

        public static string SerializeProjectSettings(AgentProjectSettingsDocument settings) =>
            AgentJson.Stringify(AgentJson.Object(
                ("schemaVersion", AgentProjectSettingsDocument.CurrentSchemaVersion),
                ("permissionMode", settings.PermissionMode.ToString()),
                ("editorSystemPrompt", settings.EditorSystemPrompt),
                ("runtimeSystemPrompt", settings.RuntimeSystemPrompt),
                ("defaultToolTimeoutSeconds", settings.DefaultToolTimeoutSeconds),
                ("maximumAgentSteps", settings.MaximumAgentSteps),
                ("agentsRoots", settings.AgentsRoots.Select(ToJson).Cast<object?>().ToList()),
                ("skillRoots", settings.SkillRoots.Select(ToJson).Cast<object?>().ToList())));

        public static AgentProjectSettingsDocument DeserializeProjectSettings(
            string json,
            AgentProjectSettingsDocument? packageDefaults = null)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new FormatException("Yuze Agent Tool Project Settings JSON is empty.");
            var root = AgentJson.ParseObject(json);
            var version = AgentJson.GetSchemaVersion(root);
            if (version > AgentProjectSettingsDocument.CurrentSchemaVersion)
                throw new FormatException(
                    $"Project Settings schema version {version} is newer than the supported version " +
                    $"{AgentProjectSettingsDocument.CurrentSchemaVersion}.");
            var result = new AgentProjectSettingsDocument
            {
                SchemaVersion = AgentProjectSettingsDocument.CurrentSchemaVersion,
                PermissionMode = ReadRequiredEnum(root, "permissionMode", packageDefaults?.PermissionMode),
                EditorSystemPrompt = ReadRequiredString(root, "editorSystemPrompt",
                    packageDefaults?.EditorSystemPrompt),
                RuntimeSystemPrompt = ReadRequiredString(root, "runtimeSystemPrompt",
                    packageDefaults?.RuntimeSystemPrompt),
                DefaultToolTimeoutSeconds = ReadRequiredInt(root, "defaultToolTimeoutSeconds",
                    packageDefaults?.DefaultToolTimeoutSeconds),
                MaximumAgentSteps = ReadRequiredInt(root, "maximumAgentSteps",
                    packageDefaults?.MaximumAgentSteps),
                AgentsRoots = root.ContainsKey("agentsRoots")
                    ? AgentJson.GetObjectArray(root, "agentsRoots")
                        .Select(ReadPathLocation).ToList()
                    : CloneRoots(packageDefaults?.AgentsRoots, "agentsRoots"),
                SkillRoots = root.ContainsKey("skillRoots")
                    ? AgentJson.GetObjectArray(root, "skillRoots")
                        .Select(ReadPathLocation).ToList()
                    : CloneRoots(packageDefaults?.SkillRoots, "skillRoots")
            };
            return result;
        }

        private static string ReadRequiredString(
            Dictionary<string, object?> root,
            string key,
            string? defaultValue)
        {
            if (root.ContainsKey(key)) return AgentJson.GetString(root, key);
            if (defaultValue != null) return defaultValue;
            throw new FormatException($"JSON property '{key}' is required.");
        }

        private static int ReadRequiredInt(
            Dictionary<string, object?> root,
            string key,
            int? defaultValue)
        {
            if (root.ContainsKey(key)) return EvalData.GetInt(root, key);
            if (defaultValue.HasValue) return defaultValue.Value;
            throw new FormatException($"JSON property '{key}' is required.");
        }

        private static TEnum ReadRequiredEnum<TEnum>(
            Dictionary<string, object?> root,
            string key,
            TEnum? defaultValue)
            where TEnum : struct, Enum
        {
            if (root.ContainsKey(key))
                return AgentJson.GetEnum(root, key, defaultValue ?? default);
            if (defaultValue.HasValue) return defaultValue.Value;
            throw new FormatException($"JSON property '{key}' is required.");
        }

        private static bool ReadRequiredBool(Dictionary<string, object?> root, string key)
        {
            if (!root.ContainsKey(key))
                throw new FormatException($"JSON property '{key}' is required.");
            if (root[key] is bool value) return value;
            throw new FormatException($"JSON property '{key}' must be a Boolean value.");
        }

        private static List<AgentPathLocation> CloneRoots(
            IReadOnlyList<AgentPathLocation>? roots,
            string propertyName)
        {
            if (roots == null)
                throw new FormatException($"JSON property '{propertyName}' is required.");
            return roots.Select(CloneRoot).ToList();
        }

        private static AgentPathLocation CloneRoot(AgentPathLocation value)
        {
            return new AgentPathLocation
            {
                Id = value.Id,
                BasePath = value.BasePath,
                UseUnityAgentToolDirectory = value.UseUnityAgentToolDirectory,
                RelativePath = value.RelativePath,
                Scope = value.Scope,
                EmbedInPlayerBuild = value.EmbedInPlayerBuild
            };
        }

        private static Dictionary<string, object?> ToMachineJson(AgentSettingsDocument settings)
        {
            return AgentJson.Object(
                ("schemaVersion", AgentSettingsDocument.CurrentSchemaVersion),
                ("permissionMode", settings.PermissionMode.ToString()),
                ("editorSystemPrompt", settings.EditorSystemPrompt),
                ("runtimeSystemPrompt", settings.RuntimeSystemPrompt),
                ("defaultToolTimeoutSeconds", settings.DefaultToolTimeoutSeconds),
                ("maximumAgentSteps", settings.MaximumAgentSteps),
                ("agentsRoots", settings.AgentsRoots.Select(ToJson).Cast<object?>().ToList()),
                ("skillRoots", settings.SkillRoots.Select(ToJson).Cast<object?>().ToList()));
        }

        private static Dictionary<string, object?> ToProviderJson(AgentProviderSettingsDocument settings)
        {
            return AgentJson.Object(
                ("schemaVersion", AgentProviderSettingsDocument.CurrentSchemaVersion),
                ("defaultProviderProfileId", settings.DefaultProviderProfileId),
                ("providerProfiles", settings.ProviderProfiles.Select(ToJson).Cast<object?>().ToList()));
        }

        private static Dictionary<string, object?> ToJson(AgentSettingsDocument settings)
        {
            return AgentJson.Object(
                ("schemaVersion", AgentSettingsDocument.CurrentSchemaVersion),
                ("defaultProviderProfileId", settings.DefaultProviderProfileId),
                ("permissionMode", settings.PermissionMode.ToString()),
                ("editorSystemPrompt", settings.EditorSystemPrompt),
                ("runtimeSystemPrompt", settings.RuntimeSystemPrompt),
                ("defaultToolTimeoutSeconds", settings.DefaultToolTimeoutSeconds),
                ("maximumAgentSteps", settings.MaximumAgentSteps),
                ("providerProfiles", settings.ProviderProfiles.Select(ToJson).Cast<object?>().ToList()),
                ("agentsRoots", settings.AgentsRoots.Select(ToJson).Cast<object?>().ToList()),
                ("skillRoots", settings.SkillRoots.Select(ToJson).Cast<object?>().ToList()));
        }

        private static Dictionary<string, object?> ToJson(AgentProviderProfile profile)
        {
            return AgentJson.Object(
                ("id", profile.Id),
                ("providerPresetId", profile.ProviderPresetId),
                ("name", profile.Name),
                ("protocol", profile.Protocol),
                ("baseUrl", profile.BaseUrl),
                ("model", profile.Model),
                ("reasoningEffort", profile.ReasoningEffort),
                ("apiKey", profile.ApiKey),
                ("maxOutputTokens", profile.MaxOutputTokens),
                ("contextWindowTokens", profile.ContextWindowTokens),
                ("strictTools", profile.StrictTools));
        }

        private static AgentProviderProfile ReadProviderProfile(Dictionary<string, object?> value)
        {
            var protocol = AgentJson.GetString(value, "protocol", AgentProtocolIds.OpenAiResponses);
            var baseUrl = AgentJson.GetString(value, "baseUrl", "https://api.openai.com/v1/");
            var persistedPresetId = AgentJson.GetString(value, "providerPresetId");
            var presetId = persistedPresetId;
            if (string.IsNullOrWhiteSpace(persistedPresetId))
                presetId = InferProviderPresetId(protocol, baseUrl);
            var profile = new AgentProviderProfile
            {
                Id = AgentJson.GetString(value, "id", Guid.NewGuid().ToString("N")),
                ProviderPresetId = presetId,
                Name = AgentJson.GetString(value, "name", "Provider"),
                Protocol = protocol,
                BaseUrl = baseUrl,
                Model = AgentJson.GetString(value, "model"),
                ReasoningEffort = AgentJson.GetString(value, "reasoningEffort"),
                ApiKey = AgentJson.GetString(value, "apiKey"),
                MaxOutputTokens = Math.Max(1, EvalData.GetInt(value, "maxOutputTokens", 4096)),
                ContextWindowTokens = Math.Max(8_192,
                    EvalData.GetInt(value, "contextWindowTokens", 128_000)),
                StrictTools = EvalData.GetBool(value, "strictTools", true)
            };
            if (string.Equals(protocol, LegacyCodexAppServerProtocol, StringComparison.Ordinal))
            {
                var previousModel = profile.Model;
                var previousReasoningEffort = profile.ReasoningEffort;
                var previousMaxOutputTokens = profile.MaxOutputTokens;
                var previousContextWindowTokens = profile.ContextWindowTokens;
                if (!AgentProviderCatalog.ApplyPreset(profile, "openai"))
                    throw new InvalidOperationException("The built-in OpenAI Provider preset is missing.");
                if (!string.IsNullOrWhiteSpace(previousModel)) profile.Model = previousModel;
                if (!string.IsNullOrWhiteSpace(previousReasoningEffort))
                    profile.ReasoningEffort = previousReasoningEffort;
                profile.MaxOutputTokens = previousMaxOutputTokens;
                profile.ContextWindowTokens = previousContextWindowTokens;
                return profile;
            }
            // V1 profiles had no preset id and were commonly materialized with an empty model.
            // Upgrade only that legacy shape to a directly usable curated default. Explicit V2
            // empty model values remain untouched so custom endpoints can still defer selection.
            if (string.IsNullOrWhiteSpace(persistedPresetId) &&
                string.IsNullOrWhiteSpace(profile.Model) &&
                !string.Equals(presetId, "custom", StringComparison.OrdinalIgnoreCase))
                AgentProviderCatalog.ApplyPreset(profile, presetId);
            return profile;
        }

        private static string InferProviderPresetId(string protocol, string baseUrl)
        {
            foreach (var preset in AgentProviderCatalog.Providers)
            {
                if (string.Equals(preset.Protocol, protocol, StringComparison.Ordinal) &&
                    string.Equals(preset.BaseUrl.TrimEnd('/'), (baseUrl ?? string.Empty).TrimEnd('/'),
                        StringComparison.OrdinalIgnoreCase))
                    return preset.Id;
            }
            return "custom";
        }

        private static Dictionary<string, object?> ToJson(AgentPathLocation location)
        {
            return AgentJson.Object(
                ("id", location.Id),
                ("basePath", location.BasePath.ToString()),
                ("useUnityAgentToolDirectory", location.UseUnityAgentToolDirectory),
                ("relativePath", location.RelativePath),
                ("scope", location.Scope.ToString()),
                ("embedInPlayerBuild", location.EmbedInPlayerBuild));
        }

        private static AgentPathLocation ReadPathLocation(Dictionary<string, object?> value)
        {
            var location = new AgentPathLocation
            {
                Id = ReadRequiredString(value, "id", null),
                BasePath = ReadRequiredEnum<AgentPathBase>(value, "basePath", null),
                UseUnityAgentToolDirectory = ReadRequiredBool(value, "useUnityAgentToolDirectory"),
                RelativePath = ReadRequiredString(value, "relativePath", null),
                Scope = ReadRequiredEnum<AgentPathScope>(value, "scope", null),
                EmbedInPlayerBuild = ReadRequiredBool(value, "embedInPlayerBuild")
            };
            AgentPaths.Validate(location);
            return location;
        }

        private static Dictionary<string, object?> ToJson(AgentSessionDocument session)
        {
            return AgentJson.Object(
                ("schemaVersion", AgentSessionDocument.CurrentSchemaVersion),
                ("id", session.Id),
                ("title", session.Title),
                ("createdAtUtc", AgentJson.Utc(session.CreatedAtUtc)),
                ("updatedAtUtc", AgentJson.Utc(session.UpdatedAtUtc)),
                ("providerProfileId", session.ProviderProfileId),
                ("model", session.Model),
                ("reasoningEffort", session.ReasoningEffort),
                ("permissionMode", session.PermissionMode.ToString()),
                ("systemPrompt", session.SystemPrompt),
                ("workingDirectory", session.WorkingDirectory),
                ("providerThreadId", session.ProviderThreadId),
                ("state", session.State.ToString()),
                ("messages", session.Messages.Select(ToJson).Cast<object?>().ToList()),
                ("summary", session.Summary),
                ("summarizedMessageCount", session.SummarizedMessageCount),
                ("contextSummaryMessageCount", session.ContextSummaryMessageCount),
                ("completedSteps", session.CompletedSteps),
                ("usage", AgentJson.Object(
                    ("inputTokens", session.Usage.InputTokens),
                    ("outputTokens", session.Usage.OutputTokens))),
                ("lastError", session.LastError),
                ("pendingApproval", session.PendingApproval == null ? null : ToJson(session.PendingApproval)),
                ("isPinned", session.IsPinned),
                ("isArchived", session.IsArchived),
                ("sortOrder", session.SortOrder),
                ("draft", session.Draft));
        }

        private static Dictionary<string, object?> ToJson(AgentMessage message)
        {
            return AgentJson.Object(
                ("id", message.Id),
                ("role", message.Role.ToString()),
                ("text", message.Text),
                ("toolCalls", message.ToolCalls.Select(ToJson).Cast<object?>().ToList()),
                ("toolCallId", message.ToolCallId),
                ("toolName", message.ToolName),
                ("isError", message.IsError),
                ("providerDataJson", message.ProviderDataJson),
                ("createdAtUtc", AgentJson.Utc(message.CreatedAtUtc)));
        }

        private static AgentMessage ReadMessage(Dictionary<string, object?> value)
        {
            var message = new AgentMessage
            {
                Id = AgentJson.GetString(value, "id", Guid.NewGuid().ToString("N")),
                Role = AgentJson.GetEnum(value, "role", AgentMessageRole.User),
                Text = AgentJson.GetString(value, "text"),
                ToolCallId = AgentJson.GetString(value, "toolCallId"),
                ToolName = AgentJson.GetString(value, "toolName"),
                IsError = EvalData.GetBool(value, "isError"),
                ProviderDataJson = AgentJson.GetString(value, "providerDataJson"),
                CreatedAtUtc = AgentJson.GetDateTime(value, "createdAtUtc", DateTime.UtcNow)
            };
            foreach (var call in AgentJson.GetObjectArray(value, "toolCalls"))
                message.ToolCalls.Add(ReadToolCall(call));
            return message;
        }

        private static Dictionary<string, object?> ToJson(AgentToolCall call)
        {
            return AgentJson.Object(
                ("id", call.Id),
                ("name", call.Name),
                ("argumentsJson", call.ArgumentsJson),
                ("providerItemId", call.ProviderItemId));
        }

        private static AgentToolCall ReadToolCall(Dictionary<string, object?> value)
        {
            return new AgentToolCall
            {
                Id = AgentJson.GetString(value, "id"),
                Name = AgentJson.GetString(value, "name"),
                ArgumentsJson = AgentJson.GetString(value, "argumentsJson", "{}"),
                ProviderItemId = AgentJson.GetString(value, "providerItemId")
            };
        }

        private static Dictionary<string, object?> ToJson(AgentApprovalRequest approval)
        {
            return AgentJson.Object(
                ("id", approval.Id),
                ("sessionId", approval.SessionId),
                ("toolCallId", approval.ToolCallId),
                ("toolName", approval.ToolName),
                ("argumentsJson", approval.ArgumentsJson),
                ("description", approval.Description),
                ("createdAtUtc", AgentJson.Utc(approval.CreatedAtUtc)));
        }

        private static AgentApprovalRequest ReadApproval(Dictionary<string, object?> value)
        {
            return new AgentApprovalRequest
            {
                Id = AgentJson.GetString(value, "id", Guid.NewGuid().ToString("N")),
                SessionId = AgentJson.GetString(value, "sessionId"),
                ToolCallId = AgentJson.GetString(value, "toolCallId"),
                ToolName = AgentJson.GetString(value, "toolName"),
                ArgumentsJson = AgentJson.GetString(value, "argumentsJson", "{}"),
                Description = AgentJson.GetString(value, "description"),
                CreatedAtUtc = AgentJson.GetDateTime(value, "createdAtUtc", DateTime.UtcNow)
            };
        }

    }
}
