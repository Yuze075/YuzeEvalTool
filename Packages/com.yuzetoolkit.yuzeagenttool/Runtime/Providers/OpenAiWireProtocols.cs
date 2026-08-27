#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace YuzeToolkit.UnityAgent
{
    internal sealed class OpenAiResponsesWireProtocol : AgentWireProtocolBase
    {
        public override string TurnPath => "responses";

        public override Dictionary<string, string> CreateHeaders(string secret)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(secret)) headers["Authorization"] = "Bearer " + secret;
            return headers;
        }

        public override Dictionary<string, object?> CreateRequest(
            AgentProviderProfile profile,
            AgentModelRequest request)
        {
            Validate(profile, request);
            var input = new List<object?>();
            foreach (var message in request.Messages)
            {
                if (message.Role == AgentMessageRole.Tool)
                {
                    if (string.IsNullOrWhiteSpace(message.ToolCallId))
                        throw new AgentProviderException("OpenAI Responses tool output is missing its call id.");
                    input.Add(AgentJson.Object(
                        ("type", "function_call_output"),
                        ("call_id", message.ToolCallId),
                        ("output", message.Text)));
                    continue;
                }

                if (message.Role == AgentMessageRole.Assistant)
                {
                    foreach (var item in AgentProviderDataEnvelope.Parse(message.ProviderDataJson,
                                 AgentProtocolIds.OpenAiResponses, "items"))
                    {
                        if (AgentJson.GetString(item, "type") != "reasoning")
                            throw new AgentProviderException(
                                "Stored OpenAI Responses provider data contains a non-reasoning item.");
                        if (string.IsNullOrWhiteSpace(AgentJson.GetString(item, "id")))
                            throw new AgentProviderException(
                                "Stored OpenAI Responses reasoning item is missing its id.");
                        input.Add(item);
                    }
                }

                if (!string.IsNullOrEmpty(message.Text))
                {
                    input.Add(AgentJson.Object(
                        ("role", message.Role == AgentMessageRole.User ? "user" : "assistant"),
                        ("content", message.Text)));
                }

                foreach (var call in message.ToolCalls)
                {
                    ValidateToolCall(call, "OpenAI Responses");
                    var functionCall = AgentJson.Object(
                        ("type", "function_call"),
                        ("call_id", call.Id),
                        ("name", call.Name),
                        ("arguments", call.ArgumentsJson));
                    if (!string.IsNullOrWhiteSpace(call.ProviderItemId))
                        functionCall["id"] = call.ProviderItemId;
                    input.Add(functionCall);
                }
            }

            var compatibility = AgentProviderCatalog.ResolveCompatibility(profile);
            var root = AgentJson.Object(
                ("model", Model(profile, request)),
                ("instructions", request.SystemPrompt),
                ("input", input),
                ("stream", true),
                ("max_output_tokens", Math.Max(1, request.MaxOutputTokens)));
            if (compatibility.IncludeEncryptedReasoning)
                root["include"] = AgentJson.Array("reasoning.encrypted_content");
            if (compatibility.IncludeParallelToolCalls) root["parallel_tool_calls"] = false;
            if (request.Tools.Count > 0) root["tools"] = OpenAiTools(profile, request);
            var effort = Effort(profile, request);
            if (compatibility.ResponsesReasoning && HasEffort(effort))
                root["reasoning"] = AgentJson.Object(("effort", effort));
            return root;
        }

        public override IAgentWireDecoder CreateDecoder(Action<AgentStreamEvent>? onEvent) =>
            new OpenAiResponsesDecoder(onEvent);
    }

    internal sealed class OpenAiResponsesDecoder : IAgentWireDecoder
    {
        private sealed class PendingCall
        {
            public int Index = int.MaxValue;
            public string ItemId = string.Empty;
            public string CallId = string.Empty;
            public string Name = string.Empty;
            public bool Started;
            public readonly StringBuilder Arguments = new();
        }

        private readonly Action<AgentStreamEvent>? _onEvent;
        private readonly StringBuilder _text = new();
        private readonly Dictionary<string, PendingCall> _calls = new(StringComparer.Ordinal);
        private readonly SortedDictionary<int, Dictionary<string, object?>> _reasoningItems = new();
        private AgentUsage _usage = new();
        private bool _terminal;
        private string _finishReason = string.Empty;

        public OpenAiResponsesDecoder(Action<AgentStreamEvent>? onEvent)
        {
            _onEvent = onEvent;
            _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.RunStarted));
        }

        public void Accept(SseEvent value)
        {
            if (string.IsNullOrWhiteSpace(value.Data) || value.Data == "[DONE]") return;
            var root = AgentJson.ParseObject(value.Data);
            var type = string.IsNullOrWhiteSpace(value.Name) ? AgentJson.GetString(root, "type") : value.Name;
            switch (type)
            {
                case "response.output_text.delta":
                {
                    var delta = AgentJson.GetString(root, "delta");
                    _text.Append(delta);
                    _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.TextDelta, delta));
                    break;
                }
                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                {
                    var delta = AgentJson.GetString(root, "delta");
                    _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.ReasoningDelta, delta));
                    break;
                }
                case "response.output_item.added":
                    ReadItem(root);
                    break;
                case "response.output_item.done":
                    ReadItem(root);
                    ReadReasoningItem(root);
                    break;
                case "response.function_call_arguments.delta":
                {
                    var call = GetCall(root);
                    var delta = AgentJson.GetString(root, "delta");
                    call.Arguments.Append(delta);
                    _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.ToolCallArgumentsDelta, delta,
                        string.IsNullOrWhiteSpace(call.CallId) ? call.ItemId : call.CallId));
                    break;
                }
                case "response.function_call_arguments.done":
                {
                    var call = GetCall(root);
                    var arguments = AgentJson.GetString(root, "arguments");
                    if (!string.IsNullOrWhiteSpace(arguments))
                    {
                        call.Arguments.Clear();
                        call.Arguments.Append(arguments);
                    }
                    break;
                }
                case "response.completed":
                {
                    _terminal = true;
                    _finishReason = "completed";
                    var response = AgentJson.GetObject(root, "response");
                    if (response != null)
                    {
                        ReadUsage(response);
                        ReadCompletedOutput(response);
                    }
                    break;
                }
                case "response.incomplete":
                {
                    var response = AgentJson.GetObject(root, "response");
                    if (response != null) ReadUsage(response);
                    var details = response == null ? null : AgentJson.GetObject(response, "incomplete_details");
                    var reason = details == null
                        ? "unknown reason"
                        : AgentJson.GetString(details, "reason", "unknown reason");
                    throw Failure("OpenAI Responses request was incomplete: " + reason + ".");
                }
                case "response.failed":
                case "error":
                {
                    var error = AgentJson.GetObject(root, "error") ??
                                AgentJson.GetObject(AgentJson.GetObject(root, "response") ?? root, "error");
                    var message = error == null
                        ? AgentJson.GetString(root, "message", value.Data)
                        : AgentJson.GetString(error, "message", value.Data);
                    throw Failure("OpenAI Responses request failed: " + message);
                }
            }
        }

        public AgentModelResponse Complete()
        {
            if (!_terminal)
                throw Failure("OpenAI Responses stream ended before response.completed.");
            var providerItems = new List<object?>(_reasoningItems.Count);
            foreach (var item in _reasoningItems.Values)
            {
                if (AgentJson.GetString(item, "type") != "reasoning" ||
                    string.IsNullOrWhiteSpace(AgentJson.GetString(item, "id")))
                    throw Failure("OpenAI Responses returned an invalid reasoning output item.");
                providerItems.Add(item);
            }
            var response = new AgentModelResponse
            {
                Text = _text.ToString(),
                Usage = _usage,
                FinishReason = _finishReason,
                ProviderDataJson = AgentProviderDataEnvelope.Create(
                    AgentProtocolIds.OpenAiResponses,
                    "items",
                    providerItems)
            };
            foreach (var call in _calls.Values.OrderBy(value => value.Index))
            {
                if (string.IsNullOrWhiteSpace(call.CallId))
                    throw Failure("OpenAI Responses returned a function call without a call id.");
                if (string.IsNullOrWhiteSpace(call.Name))
                    throw Failure($"OpenAI Responses function call '{call.CallId}' is missing its name.");
                NotifyStarted(call);
                response.ToolCalls.Add(new AgentToolCall
                {
                    Id = call.CallId,
                    Name = call.Name,
                    ArgumentsJson = call.Arguments.Length == 0 ? "{}" : call.Arguments.ToString(),
                    ProviderItemId = call.ItemId
                });
            }

            _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.RunCompleted));
            return response;
        }

        private void ReadItem(Dictionary<string, object?> root)
        {
            var item = AgentJson.GetObject(root, "item");
            if (item == null || AgentJson.GetString(item, "type") != "function_call") return;
            ReadItem(item, EvalData.GetInt(root, "output_index"));
        }

        private void ReadItem(Dictionary<string, object?> item, int outputIndex)
        {
            var itemId = AgentJson.GetString(item, "id");
            var call = GetCall(outputIndex, itemId);
            call.ItemId = AgentJson.GetString(item, "id", call.ItemId);
            call.CallId = AgentJson.GetString(item, "call_id", call.CallId);
            call.Name = AgentJson.GetString(item, "name", call.Name);
            var arguments = AgentJson.GetString(item, "arguments");
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                call.Arguments.Clear();
                call.Arguments.Append(arguments);
            }
            NotifyStarted(call);
        }

        private void ReadReasoningItem(Dictionary<string, object?> root)
        {
            var item = AgentJson.GetObject(root, "item");
            if (item == null || AgentJson.GetString(item, "type") != "reasoning") return;
            _reasoningItems[EvalData.GetInt(root, "output_index")] = item;
        }

        private void ReadCompletedOutput(Dictionary<string, object?> response)
        {
            var output = AgentJson.GetArray(response, "output");
            if (output == null) return;
            for (var index = 0; index < output.Count; index++)
            {
                if (EvalData.AsObject(output[index]) is not { } item) continue;
                var type = AgentJson.GetString(item, "type");
                if (type == "reasoning")
                    _reasoningItems[index] = item;
                else if (type == "function_call")
                    ReadItem(item, index);
            }
        }

        private PendingCall GetCall(Dictionary<string, object?> root, string itemId = "")
        {
            if (string.IsNullOrWhiteSpace(itemId)) itemId = AgentJson.GetString(root, "item_id");
            return GetCall(EvalData.GetInt(root, "output_index"), itemId);
        }

        private PendingCall GetCall(int outputIndex, string itemId)
        {
            var indexKey = "index:" + outputIndex;
            if (!string.IsNullOrWhiteSpace(itemId) && _calls.TryGetValue(itemId, out var byItemId))
            {
                byItemId.Index = Math.Min(byItemId.Index, outputIndex);
                return byItemId;
            }
            if (_calls.TryGetValue(indexKey, out var byIndex))
            {
                byIndex.Index = Math.Min(byIndex.Index, outputIndex);
                if (!string.IsNullOrWhiteSpace(itemId))
                {
                    _calls.Remove(indexKey);
                    _calls[itemId] = byIndex;
                    byIndex.ItemId = itemId;
                }
                return byIndex;
            }

            var key = string.IsNullOrWhiteSpace(itemId) ? indexKey : itemId;
            var call = new PendingCall { ItemId = itemId, Index = outputIndex };
            _calls.Add(key, call);
            return call;
        }

        private void NotifyStarted(PendingCall call)
        {
            if (call.Started || string.IsNullOrWhiteSpace(call.CallId) || string.IsNullOrWhiteSpace(call.Name)) return;
            call.Started = true;
            _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.ToolCallStarted, call.Name, call.CallId));
        }

        private AgentProviderException Failure(string message)
        {
            _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.RunFailed, message));
            return new AgentProviderException(message);
        }

        private void ReadUsage(Dictionary<string, object?> response)
        {
            var usage = AgentJson.GetObject(response, "usage");
            if (usage == null) return;
            _usage = new AgentUsage
            {
                InputTokens = AgentJson.GetLong(usage, "input_tokens"),
                OutputTokens = AgentJson.GetLong(usage, "output_tokens")
            };
            _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.UsageUpdated,
                _usage.TotalTokens.ToString()));
        }
    }

    internal sealed class OpenAiChatWireProtocol : AgentWireProtocolBase
    {
        public override string TurnPath => "chat/completions";

        public override Dictionary<string, string> CreateHeaders(string secret)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(secret)) headers["Authorization"] = "Bearer " + secret;
            return headers;
        }

        public override Dictionary<string, object?> CreateRequest(
            AgentProviderProfile profile,
            AgentModelRequest request)
        {
            Validate(profile, request);
            var messages = new List<object?>
            {
                AgentJson.Object(("role", "system"), ("content", request.SystemPrompt))
            };
            foreach (var message in request.Messages)
            {
                if (message.Role == AgentMessageRole.Tool)
                {
                    if (string.IsNullOrWhiteSpace(message.ToolCallId))
                        throw new AgentProviderException("OpenAI Chat tool output is missing its call id.");
                    messages.Add(AgentJson.Object(
                        ("role", "tool"),
                        ("tool_call_id", message.ToolCallId),
                        ("content", message.Text)));
                    continue;
                }

                var value = AgentJson.Object(
                    ("role", message.Role == AgentMessageRole.User ? "user" : "assistant"),
                    ("content", message.Text));
                if (message.Role == AgentMessageRole.Assistant)
                {
                    var providerFields = AgentProviderDataEnvelope.Parse(message.ProviderDataJson,
                        AgentProtocolIds.OpenAiChat, "fields");
                    if (providerFields.Count > 1)
                        throw new AgentProviderException(
                            "Stored OpenAI Chat provider data contains more than one field set.");
                    if (providerFields.Count == 1)
                    {
                        var reasoningContent = AgentJson.GetString(providerFields[0], "reasoning_content");
                        if (!string.IsNullOrEmpty(reasoningContent))
                            value["reasoning_content"] = reasoningContent;
                    }
                }
                if (message.ToolCalls.Count > 0)
                {
                    foreach (var call in message.ToolCalls)
                        ValidateToolCall(call, "OpenAI Chat");
                    value["tool_calls"] = message.ToolCalls.Select(call => (object?)AgentJson.Object(
                        ("id", call.Id),
                        ("type", "function"),
                        ("function", AgentJson.Object(
                            ("name", call.Name),
                            ("arguments", call.ArgumentsJson))))).ToList();
                }
                messages.Add(value);
            }

            var compatibility = AgentProviderCatalog.ResolveCompatibility(profile);
            var root = AgentJson.Object(
                ("model", Model(profile, request)),
                ("messages", messages),
                ("stream", true),
                (compatibility.ChatTokenParameter == AgentChatTokenParameter.MaxCompletionTokens
                    ? "max_completion_tokens"
                    : "max_tokens", Math.Max(1, request.MaxOutputTokens)));
            if (compatibility.IncludeStreamOptions)
                root["stream_options"] = AgentJson.Object(("include_usage", true));
            if (compatibility.IncludeReasoningSplit) root["reasoning_split"] = true;
            if (request.Tools.Count > 0)
            {
                root["tools"] = ChatTools(profile, request);
                if (compatibility.IncludeParallelToolCalls) root["parallel_tool_calls"] = false;
            }
            var effort = Effort(profile, request);
            ApplyReasoning(root, compatibility, effort);
            return root;
        }

        public override IAgentWireDecoder CreateDecoder(Action<AgentStreamEvent>? onEvent) =>
            new OpenAiChatDecoder(onEvent);

        private static void ApplyReasoning(
            Dictionary<string, object?> root,
            AgentWireCompatibility compatibility,
            string effort)
        {
            if (!HasEffort(effort) || compatibility.ChatReasoningStyle == AgentChatReasoningStyle.None) return;
            if (compatibility.ChatReasoningStyle == AgentChatReasoningStyle.ReasoningEffort)
            {
                root["reasoning_effort"] = effort;
                return;
            }

            if (compatibility.ChatReasoningStyle == AgentChatReasoningStyle.AdaptiveThinkingToggle)
            {
                root["thinking"] = AgentJson.Object(("type",
                    string.Equals(effort, "disabled", StringComparison.OrdinalIgnoreCase)
                        ? "disabled"
                        : "adaptive"));
                return;
            }

            if (string.Equals(effort, "disabled", StringComparison.OrdinalIgnoreCase))
            {
                root["thinking"] = AgentJson.Object(("type", "disabled"));
                return;
            }

            root["thinking"] = AgentJson.Object(("type", "enabled"));
            if (compatibility.ChatReasoningStyle == AgentChatReasoningStyle.ThinkingToggleAndEffort &&
                !string.Equals(effort, "enabled", StringComparison.OrdinalIgnoreCase))
                root["reasoning_effort"] = effort;
        }
    }

    internal sealed class OpenAiChatDecoder : IAgentWireDecoder
    {
        private sealed class PendingCall
        {
            public string Id = string.Empty;
            public string Name = string.Empty;
            public bool Started;
            public readonly StringBuilder Arguments = new();
        }

        private readonly Action<AgentStreamEvent>? _onEvent;
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _reasoning = new();
        private readonly SortedDictionary<int, PendingCall> _calls = new();
        private AgentUsage _usage = new();
        private bool _terminal;
        private string _finishReason = string.Empty;

        public OpenAiChatDecoder(Action<AgentStreamEvent>? onEvent)
        {
            _onEvent = onEvent;
            _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.RunStarted));
        }

        public void Accept(SseEvent value)
        {
            if (value.Data == "[DONE]")
            {
                _terminal = true;
                return;
            }
            if (string.IsNullOrWhiteSpace(value.Data)) return;
            var root = AgentJson.ParseObject(value.Data);
            if (AgentJson.GetObject(root, "error") is { } error)
            {
                var message = AgentJson.GetString(error, "message", value.Data);
                throw Failure("OpenAI Chat request failed: " + message);
            }

            if (AgentJson.GetObject(root, "usage") is { } usage)
            {
                _usage = new AgentUsage
                {
                    InputTokens = AgentJson.GetLong(usage, "prompt_tokens"),
                    OutputTokens = AgentJson.GetLong(usage, "completion_tokens")
                };
                _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.UsageUpdated,
                    _usage.TotalTokens.ToString()));
            }

            var choices = AgentJson.GetArray(root, "choices");
            if (choices == null || choices.Count == 0 || EvalData.AsObject(choices[0]) is not { } choice) return;
            var finishReason = AgentJson.GetString(choice, "finish_reason");
            if (!string.IsNullOrWhiteSpace(finishReason)) _finishReason = finishReason;
            var delta = AgentJson.GetObject(choice, "delta");
            if (delta == null) return;
            var content = AgentJson.GetString(delta, "content");
            if (!string.IsNullOrEmpty(content))
            {
                _text.Append(content);
                _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.TextDelta, content));
            }
            var reasoningContent = AgentJson.GetString(delta, "reasoning_content");
            if (!string.IsNullOrEmpty(reasoningContent))
            {
                _reasoning.Append(reasoningContent);
                _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.ReasoningDelta, reasoningContent));
            }

            foreach (var toolValue in AgentJson.Objects(AgentJson.GetArray(delta, "tool_calls")))
            {
                var index = EvalData.GetInt(toolValue, "index");
                if (!_calls.TryGetValue(index, out var call))
                {
                    call = new PendingCall();
                    _calls.Add(index, call);
                }
                var id = AgentJson.GetString(toolValue, "id");
                if (!string.IsNullOrEmpty(id)) call.Id += id;
                if (AgentJson.GetObject(toolValue, "function") is not { } function) continue;
                var name = AgentJson.GetString(function, "name");
                if (!string.IsNullOrEmpty(name)) call.Name += name;
                NotifyStarted(call);
                var arguments = AgentJson.GetString(function, "arguments");
                call.Arguments.Append(arguments);
                if (!string.IsNullOrEmpty(arguments))
                    _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.ToolCallArgumentsDelta, arguments,
                        call.Id));
            }
        }

        public AgentModelResponse Complete()
        {
            if (!_terminal)
                throw Failure("OpenAI Chat stream ended before [DONE].");
            if (string.IsNullOrWhiteSpace(_finishReason))
                throw Failure("OpenAI Chat stream ended without a finish reason.");
            if (_finishReason is "length" or "content_filter")
                throw Failure($"OpenAI Chat response stopped with finish_reason '{_finishReason}'.");
            var response = new AgentModelResponse
            {
                Text = _text.ToString(),
                Usage = _usage,
                FinishReason = _finishReason,
                ProviderDataJson = _reasoning.Length == 0
                    ? string.Empty
                    : AgentProviderDataEnvelope.Create(
                        AgentProtocolIds.OpenAiChat,
                        "fields",
                        new object?[] { AgentJson.Object(("reasoning_content", _reasoning.ToString())) })
            };
            foreach (var call in _calls.Values)
            {
                if (string.IsNullOrWhiteSpace(call.Id))
                    throw Failure("OpenAI Chat returned a tool call without a call id.");
                if (string.IsNullOrWhiteSpace(call.Name))
                    throw Failure($"OpenAI Chat tool call '{call.Id}' is missing its name.");
                NotifyStarted(call);
                response.ToolCalls.Add(new AgentToolCall
                {
                    Id = call.Id,
                    Name = call.Name,
                    ArgumentsJson = call.Arguments.Length == 0 ? "{}" : call.Arguments.ToString()
                });
            }
            if (_finishReason == "tool_calls" && response.ToolCalls.Count == 0)
                throw Failure("OpenAI Chat finished for tool calls but returned no complete tool call.");
            _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.RunCompleted));
            return response;
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
