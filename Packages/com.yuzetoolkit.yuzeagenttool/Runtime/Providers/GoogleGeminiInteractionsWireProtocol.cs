#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace YuzeToolkit.UnityAgent
{
    internal sealed class GoogleGeminiInteractionsWireProtocol : AgentWireProtocolBase
    {
        public override string TurnPath => "interactions";

        public override Dictionary<string, string> CreateHeaders(string secret)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(secret)) headers["x-goog-api-key"] = secret;
            return headers;
        }

        public override Dictionary<string, object?> CreateRequest(
            AgentProviderProfile profile,
            AgentModelRequest request)
        {
            Validate(profile, request);
            var root = AgentJson.Object(
                ("model", Model(profile, request)),
                ("input", BuildInput(request)),
                ("system_instruction", request.SystemPrompt),
                ("stream", true),
                ("store", true),
                ("thinking_summaries", "auto"));
            if (!string.IsNullOrWhiteSpace(request.ProviderThreadId))
                root["previous_interaction_id"] = request.ProviderThreadId;

            var generation = AgentJson.Object(
                ("max_output_tokens", Math.Max(1, request.MaxOutputTokens)));
            var effort = Effort(profile, request);
            if (HasEffort(effort)) generation["thinking_level"] = effort;
            root["generation_config"] = generation;

            if (request.Tools.Count > 0)
            {
                root["tools"] = request.Tools.Select(tool => (object?)AgentJson.Object(
                    ("type", "function"),
                    ("name", tool.Name),
                    ("description", tool.Description),
                    ("parameters", tool.Parameters))).ToList();
            }
            return root;
        }

        public override IAgentWireDecoder CreateDecoder(Action<AgentStreamEvent>? onEvent) =>
            new GoogleGeminiInteractionsDecoder(onEvent);

        public override IReadOnlyList<string> ParseModels(string json)
        {
            var root = AgentJson.ParseObject(json);
            var result = new List<string>();
            foreach (var model in AgentJson.Objects(AgentJson.GetArray(root, "models")))
            {
                var methods = AgentJson.GetArray(model, "supportedGenerationMethods");
                if (methods != null && methods.Count > 0 &&
                    !methods.OfType<string>().Any(value =>
                        string.Equals(value, "generateContent", StringComparison.OrdinalIgnoreCase)))
                    continue;
                var name = AgentJson.GetString(model, "name");
                const string prefix = "models/";
                if (name.StartsWith(prefix, StringComparison.Ordinal)) name = name.Substring(prefix.Length);
                if (name.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)) result.Add(name);
            }
            return result.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();
        }

        private static object BuildInput(AgentModelRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.ProviderThreadId)) return BuildContinuationInput(request);
            var steps = new List<object?>();
            foreach (var message in request.Messages)
            {
                switch (message.Role)
                {
                    case AgentMessageRole.User:
                        steps.Add(UserInput(message.Text));
                        break;
                    case AgentMessageRole.Assistant:
                        foreach (var thought in AgentProviderDataEnvelope.Parse(message.ProviderDataJson,
                                     AgentProtocolIds.GoogleGeminiInteractions, "steps"))
                        {
                            if (!string.Equals(AgentJson.GetString(thought, "type"), "thought",
                                    StringComparison.Ordinal))
                                throw new AgentProviderException(
                                    "Stored Gemini provider data contains a non-thought step.");
                            steps.Add(thought);
                        }
                        if (!string.IsNullOrEmpty(message.Text))
                            steps.Add(AgentJson.Object(
                                ("type", "model_output"),
                                ("content", AgentJson.Array(TextContent(message.Text)))));
                        foreach (var call in message.ToolCalls) steps.Add(FunctionCall(call));
                        break;
                    case AgentMessageRole.Tool:
                        steps.Add(FunctionResult(message));
                        break;
                }
            }
            if (steps.Count == 0)
                throw new AgentProviderException("Gemini Interactions requires at least one input step.");
            return steps;
        }

        private static object BuildContinuationInput(AgentModelRequest request)
        {
            var trailingTools = new List<object?>();
            for (var index = request.Messages.Count - 1; index >= 0; index--)
            {
                var message = request.Messages[index];
                if (message.Role != AgentMessageRole.Tool) break;
                trailingTools.Insert(0, FunctionResult(message));
            }
            if (trailingTools.Count > 0) return trailingTools;

            for (var index = request.Messages.Count - 1; index >= 0; index--)
            {
                var message = request.Messages[index];
                if (message.Role == AgentMessageRole.User)
                    return new List<object?> { UserInput(message.Text) };
            }
            throw new AgentProviderException(
                "Gemini Interactions continuation requires a user message or tool result.");
        }

        private static Dictionary<string, object?> UserInput(string text) =>
            AgentJson.Object(
                ("type", "user_input"),
                ("content", AgentJson.Array(TextContent(text))));

        private static Dictionary<string, object?> TextContent(string text) =>
            AgentJson.Object(("type", "text"), ("text", text));

        private static Dictionary<string, object?> FunctionCall(AgentToolCall call)
        {
            ValidateToolCall(call, "Gemini Interactions");
            Dictionary<string, object?> arguments;
            try
            {
                arguments = AgentJson.ParseObject(call.ArgumentsJson);
            }
            catch (FormatException exception)
            {
                throw new AgentProviderException(
                    $"Gemini tool call '{call.Id}' contains invalid object arguments.", exception);
            }
            return AgentJson.Object(
                ("type", "function_call"),
                ("id", call.Id),
                ("name", call.Name),
                ("arguments", arguments));
        }

        private static Dictionary<string, object?> FunctionResult(AgentMessage message)
        {
            if (string.IsNullOrWhiteSpace(message.ToolCallId))
                throw new AgentProviderException("Gemini tool result is missing its call_id.");
            var value = AgentJson.Object(
                ("type", "function_result"),
                ("call_id", message.ToolCallId),
                ("result", AgentJson.Array(TextContent(message.Text))),
                ("is_error", message.IsError));
            if (!string.IsNullOrWhiteSpace(message.ToolName)) value["name"] = message.ToolName;
            return value;
        }
    }

    internal sealed class GoogleGeminiInteractionsDecoder : IAgentWireDecoder
    {
        private sealed class PendingCall
        {
            public string Id = string.Empty;
            public string Name = string.Empty;
            public bool Started;
            public readonly StringBuilder Arguments = new();
        }

        private sealed class PendingThought
        {
            public readonly StringBuilder Signature = new();
            public readonly List<object?> Summary = new();
        }

        private readonly Action<AgentStreamEvent>? _onEvent;
        private readonly StringBuilder _text = new();
        private readonly SortedDictionary<int, PendingCall> _calls = new();
        private readonly SortedDictionary<int, PendingThought> _thoughts = new();
        private AgentUsage _usage = new();
        private string _interactionId = string.Empty;
        private string _status = string.Empty;
        private bool _terminal;

        public GoogleGeminiInteractionsDecoder(Action<AgentStreamEvent>? onEvent)
        {
            _onEvent = onEvent;
            _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.RunStarted));
        }

        public void Accept(SseEvent value)
        {
            if (string.IsNullOrWhiteSpace(value.Data) || value.Data == "[DONE]") return;
            var root = AgentJson.ParseObject(value.Data);
            var type = string.IsNullOrWhiteSpace(value.Name)
                ? AgentJson.GetString(root, "event_type")
                : value.Name;
            ReadUsage(AgentJson.GetObject(root, "metadata"));
            switch (type)
            {
                case "interaction.created":
                    ReadInteraction(AgentJson.GetObject(root, "interaction"));
                    break;
                case "interaction.status_update":
                    _interactionId = AgentJson.GetString(root, "interaction_id", _interactionId);
                    _status = AgentJson.GetString(root, "status", _status);
                    break;
                case "step.start":
                    ReadStepStart(root);
                    break;
                case "step.delta":
                    ReadStepDelta(root);
                    break;
                case "interaction.completed":
                    ReadInteraction(AgentJson.GetObject(root, "interaction"));
                    _terminal = true;
                    break;
                case "error":
                {
                    var error = AgentJson.GetObject(root, "error");
                    var message = error == null
                        ? AgentJson.GetString(root, "message", value.Data)
                        : AgentJson.GetString(error, "message", value.Data);
                    throw Failure("Gemini Interactions request failed: " + message);
                }
            }
        }

        public AgentModelResponse Complete()
        {
            if (!_terminal)
                throw Failure("Gemini Interactions stream ended before interaction.completed.");
            if (_status is "failed" or "cancelled" or "incomplete")
                throw Failure($"Gemini Interactions finished with status '{_status}'.");
            if (_status is not ("completed" or "requires_action"))
                throw Failure("Gemini Interactions completed without a terminal status.");
            if (string.IsNullOrWhiteSpace(_interactionId))
                throw Failure("Gemini Interactions response is missing its interaction id.");

            var response = new AgentModelResponse
            {
                Text = _text.ToString(),
                Usage = _usage,
                FinishReason = _status,
                ProviderThreadId = _interactionId,
                ProviderDataJson = CreateProviderData()
            };
            foreach (var call in _calls.Values)
            {
                if (string.IsNullOrWhiteSpace(call.Id))
                    throw Failure("Gemini Interactions returned a function call without an id.");
                if (string.IsNullOrWhiteSpace(call.Name))
                    throw Failure($"Gemini function call '{call.Id}' is missing its name.");
                NotifyStarted(call);
                response.ToolCalls.Add(new AgentToolCall
                {
                    Id = call.Id,
                    Name = call.Name,
                    ArgumentsJson = call.Arguments.Length == 0 ? "{}" : call.Arguments.ToString()
                });
            }
            if (_status == "requires_action" && response.ToolCalls.Count == 0)
                throw Failure("Gemini Interactions requires action but returned no function call.");
            _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.RunCompleted));
            return response;
        }

        private void ReadStepStart(Dictionary<string, object?> root)
        {
            var index = EvalData.GetInt(root, "index");
            var step = AgentJson.GetObject(root, "step");
            if (step == null) return;
            var type = AgentJson.GetString(step, "type");
            if (type == "function_call")
            {
                var call = GetCall(index);
                call.Id = AgentJson.GetString(step, "id", call.Id);
                call.Name = AgentJson.GetString(step, "name", call.Name);
                if (step.TryGetValue("arguments", out var arguments) && arguments != null)
                {
                    call.Arguments.Clear();
                    call.Arguments.Append(AgentJson.Stringify(arguments));
                }
                NotifyStarted(call);
            }
            else if (type == "thought")
            {
                var thought = GetThought(index);
                thought.Signature.Append(AgentJson.GetString(step, "signature"));
                foreach (var content in AgentJson.GetArray(step, "summary") ?? new List<object?>())
                    thought.Summary.Add(content);
            }
        }

        private void ReadStepDelta(Dictionary<string, object?> root)
        {
            var delta = AgentJson.GetObject(root, "delta");
            if (delta == null) return;
            var index = EvalData.GetInt(root, "index");
            switch (AgentJson.GetString(delta, "type"))
            {
                case "text":
                {
                    var text = AgentJson.GetString(delta, "text");
                    _text.Append(text);
                    _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.TextDelta, text));
                    break;
                }
                case "arguments_delta":
                {
                    var call = GetCall(index);
                    var arguments = AgentJson.GetString(delta, "arguments");
                    if (call.Arguments.Length == 2 && call.Arguments.ToString() == "{}") call.Arguments.Clear();
                    call.Arguments.Append(arguments);
                    if (!string.IsNullOrEmpty(arguments))
                        _onEvent?.Invoke(new AgentStreamEvent(
                            AgentStreamEventKind.ToolCallArgumentsDelta, arguments, call.Id));
                    break;
                }
                case "thought_signature":
                    GetThought(index).Signature.Append(AgentJson.GetString(delta, "signature"));
                    break;
                case "thought_summary":
                {
                    var content = AgentJson.GetObject(delta, "content");
                    if (content == null) break;
                    GetThought(index).Summary.Add(content);
                    var summary = AgentJson.GetString(content, "text");
                    if (!string.IsNullOrEmpty(summary))
                        _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.ReasoningDelta, summary));
                    break;
                }
            }
            ReadUsage(AgentJson.GetObject(root, "metadata"));
        }

        private void ReadInteraction(Dictionary<string, object?>? interaction)
        {
            if (interaction == null) return;
            _interactionId = AgentJson.GetString(interaction, "id", _interactionId);
            _status = AgentJson.GetString(interaction, "status", _status);
            ReadUsage(interaction);
        }

        private void ReadUsage(Dictionary<string, object?>? container)
        {
            if (container == null) return;
            var usage = AgentJson.GetObject(container, "total_usage") ?? AgentJson.GetObject(container, "usage");
            if (usage == null && container.ContainsKey("total_input_tokens")) usage = container;
            if (usage == null) return;
            _usage = new AgentUsage
            {
                InputTokens = AgentJson.GetLong(usage, "total_input_tokens", _usage.InputTokens),
                OutputTokens = AgentJson.GetLong(usage, "total_output_tokens", _usage.OutputTokens)
            };
            _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.UsageUpdated,
                _usage.TotalTokens.ToString()));
        }

        private string CreateProviderData()
        {
            var values = new List<object?>();
            foreach (var thought in _thoughts.Values)
            {
                if (thought.Signature.Length == 0 && thought.Summary.Count == 0) continue;
                var value = AgentJson.Object(
                    ("type", "thought"),
                    ("summary", thought.Summary));
                if (thought.Signature.Length > 0) value["signature"] = thought.Signature.ToString();
                values.Add(value);
            }
            return AgentProviderDataEnvelope.Create(
                AgentProtocolIds.GoogleGeminiInteractions, "steps", values);
        }

        private PendingCall GetCall(int index)
        {
            if (_calls.TryGetValue(index, out var value)) return value;
            value = new PendingCall();
            _calls.Add(index, value);
            return value;
        }

        private PendingThought GetThought(int index)
        {
            if (_thoughts.TryGetValue(index, out var value)) return value;
            value = new PendingThought();
            _thoughts.Add(index, value);
            return value;
        }

        private void NotifyStarted(PendingCall call)
        {
            if (call.Started || string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Name)) return;
            call.Started = true;
            _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.ToolCallStarted, call.Name, call.Id));
        }

        private AgentProviderException Failure(string message)
        {
            _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.RunFailed, message));
            return new AgentProviderException(message);
        }
    }
}
