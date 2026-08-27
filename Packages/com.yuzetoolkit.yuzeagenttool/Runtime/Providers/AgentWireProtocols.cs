#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace YuzeToolkit.UnityAgent
{
    internal interface IAgentWireProtocol
    {
        string TurnPath { get; }

        string ModelsPath { get; }

        Dictionary<string, string> CreateHeaders(string secret);

        Dictionary<string, object?> CreateRequest(AgentProviderProfile profile, AgentModelRequest request);

        IAgentWireDecoder CreateDecoder(Action<AgentStreamEvent>? onEvent);

        IReadOnlyList<string> ParseModels(string json);
    }

    internal interface IAgentWireDecoder
    {
        void Accept(SseEvent value);

        AgentModelResponse Complete();
    }

    internal static class AgentProviderDataEnvelope
    {
        private const int CurrentVersion = 1;

        public static IReadOnlyList<Dictionary<string, object?>> Parse(
            string json,
            string protocol,
            string collectionName)
        {
            if (string.IsNullOrWhiteSpace(json))
                return Array.Empty<Dictionary<string, object?>>();

            try
            {
                var root = AgentJson.ParseObject(json);
                if (!string.Equals(AgentJson.GetString(root, "protocol"), protocol,
                        StringComparison.Ordinal))
                    return Array.Empty<Dictionary<string, object?>>();
                if (AgentJson.GetLong(root, "version", -1) != CurrentVersion)
                    throw new AgentProviderException(
                        $"Stored provider data for '{protocol}' has an unsupported version.");
                var values = AgentJson.GetArray(root, collectionName) ??
                             throw new AgentProviderException(
                                 $"Stored provider data for '{protocol}' is missing '{collectionName}'.");
                var result = new List<Dictionary<string, object?>>(values.Count);
                foreach (var value in values)
                {
                    if (EvalData.AsObject(value) is not { } item)
                        throw new AgentProviderException(
                            $"Stored provider data for '{protocol}' contains a non-object item.");
                    result.Add(item);
                }
                return result;
            }
            catch (AgentProviderException)
            {
                throw;
            }
            catch (Exception exception) when (exception is FormatException or OverflowException or
                                               LitJson.JsonException)
            {
                throw new AgentProviderException("Stored provider data is not valid JSON.", exception);
            }
        }

        public static string Create(
            string protocol,
            string collectionName,
            IReadOnlyList<object?> values)
        {
            if (values.Count == 0) return string.Empty;
            return AgentJson.Stringify(AgentJson.Object(
                ("protocol", protocol),
                ("version", CurrentVersion),
                (collectionName, values)));
        }
    }

    internal static class AgentWireProtocolFactory
    {
        public static IAgentWireProtocol Create(string protocol)
        {
            return protocol switch
            {
                AgentProtocolIds.OpenAiResponses => new OpenAiResponsesWireProtocol(),
                AgentProtocolIds.OpenAiChat => new OpenAiChatWireProtocol(),
                AgentProtocolIds.AnthropicMessages => new AnthropicMessagesWireProtocol(),
                AgentProtocolIds.GoogleGeminiInteractions => new GoogleGeminiInteractionsWireProtocol(),
                _ => throw new AgentProviderException($"Unknown provider protocol '{protocol}'.")
            };
        }
    }

    internal abstract class AgentWireProtocolBase : IAgentWireProtocol
    {
        public abstract string TurnPath { get; }

        public virtual string ModelsPath => "models";

        public abstract Dictionary<string, string> CreateHeaders(string secret);

        public abstract Dictionary<string, object?> CreateRequest(
            AgentProviderProfile profile,
            AgentModelRequest request);

        public abstract IAgentWireDecoder CreateDecoder(Action<AgentStreamEvent>? onEvent);

        public virtual IReadOnlyList<string> ParseModels(string json)
        {
            var root = AgentJson.ParseObject(json);
            return AgentJson.Objects(AgentJson.GetArray(root, "data"))
                .Select(value => AgentJson.GetString(value, "id"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
        }

        protected static void Validate(AgentProviderProfile profile, AgentModelRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Model) && string.IsNullOrWhiteSpace(profile.Model))
                throw new AgentProviderException("A model id is required.");
        }

        protected static List<object?> OpenAiTools(AgentProviderProfile profile, AgentModelRequest request)
        {
            var includeStrict = profile.StrictTools &&
                                AgentProviderCatalog.ResolveCompatibility(profile).SupportsStrictTools;
            return request.Tools.Select(tool =>
            {
                var value = AgentJson.Object(
                    ("type", "function"),
                    ("name", tool.Name),
                    ("description", tool.Description),
                    ("parameters", tool.Parameters));
                if (includeStrict) value["strict"] = true;
                return (object?)value;
            }).ToList();
        }

        protected static List<object?> ChatTools(AgentProviderProfile profile, AgentModelRequest request)
        {
            var includeStrict = profile.StrictTools &&
                                AgentProviderCatalog.ResolveCompatibility(profile).SupportsStrictTools;
            return request.Tools.Select(tool =>
            {
                var function = AgentJson.Object(
                    ("name", tool.Name),
                    ("description", tool.Description),
                    ("parameters", tool.Parameters));
                if (includeStrict) function["strict"] = true;
                return (object?)AgentJson.Object(
                    ("type", "function"),
                    ("function", function));
            }).ToList();
        }

        protected static string Model(AgentProviderProfile profile, AgentModelRequest request) =>
            string.IsNullOrWhiteSpace(request.Model) ? profile.Model : request.Model;

        protected static string Effort(AgentProviderProfile profile, AgentModelRequest request) =>
            string.IsNullOrWhiteSpace(request.ReasoningEffort) ? profile.ReasoningEffort : request.ReasoningEffort;

        protected static bool HasEffort(string effort) =>
            !string.IsNullOrWhiteSpace(effort) && !string.Equals(effort, "default", StringComparison.OrdinalIgnoreCase);

        protected static void ValidateToolCall(AgentToolCall call, string protocolName)
        {
            if (string.IsNullOrWhiteSpace(call.Id))
                throw new AgentProviderException($"{protocolName} tool call is missing its call id.");
            if (string.IsNullOrWhiteSpace(call.Name))
                throw new AgentProviderException($"{protocolName} tool call '{call.Id}' is missing its name.");
        }
    }
}
