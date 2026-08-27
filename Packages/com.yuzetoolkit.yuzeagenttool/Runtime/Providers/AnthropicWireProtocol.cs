#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace YuzeToolkit.UnityAgent
{
    internal sealed class AnthropicMessagesWireProtocol : AgentWireProtocolBase
    {
        public override string TurnPath => "messages";

        public override Dictionary<string, string> CreateHeaders(string secret)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["anthropic-version"] = "2023-06-01"
            };
            if (!string.IsNullOrWhiteSpace(secret)) headers["x-api-key"] = secret;
            return headers;
        }

        public override Dictionary<string, object?> CreateRequest(
            AgentProviderProfile profile,
            AgentModelRequest request)
        {
            Validate(profile, request);
            var messages = new List<object?>();
            for (var index = 0; index < request.Messages.Count; index++)
            {
                var message = request.Messages[index];
                if (message.Role == AgentMessageRole.Tool)
                {
                    var results = new List<object?>();
                    while (index < request.Messages.Count && request.Messages[index].Role == AgentMessageRole.Tool)
                    {
                        var result = request.Messages[index];
                        if (string.IsNullOrWhiteSpace(result.ToolCallId))
                            throw new AgentProviderException("Anthropic tool result is missing its tool_use_id.");
                        results.Add(AgentJson.Object(
                            ("type", "tool_result"),
                            ("tool_use_id", result.ToolCallId),
                            ("content", result.Text),
                            ("is_error", result.IsError)));
                        index++;
                    }
                    index--;
                    messages.Add(AgentJson.Object(("role", "user"), ("content", results)));
                    continue;
                }

                if (message.Role == AgentMessageRole.User)
                {
                    messages.Add(AgentJson.Object(("role", "user"), ("content", message.Text)));
                    continue;
                }

                var content = new List<object?>();
                foreach (var block in AgentProviderDataEnvelope.Parse(message.ProviderDataJson,
                             AgentProtocolIds.AnthropicMessages, "content"))
                {
                    ValidateThinkingBlock(block);
                    content.Add(block);
                }
                if (!string.IsNullOrEmpty(message.Text))
                    content.Add(AgentJson.Object(("type", "text"), ("text", message.Text)));
                foreach (var call in message.ToolCalls)
                {
                    ValidateToolCall(call, "Anthropic Messages");
                    content.Add(AgentJson.Object(
                        ("type", "tool_use"),
                        ("id", call.Id),
                        ("name", call.Name),
                        ("input", ParseToolInput(call))));
                }
                messages.Add(AgentJson.Object(("role", "assistant"), ("content", content)));
            }

            var tools = request.Tools.Select(tool => (object?)AgentJson.Object(
                ("name", tool.Name),
                ("description", tool.Description),
                ("input_schema", tool.Parameters),
                ("strict", profile.StrictTools))).ToList();
            var root = AgentJson.Object(
                ("model", Model(profile, request)),
                ("system", request.SystemPrompt),
                ("messages", messages),
                ("stream", true),
                ("max_tokens", Math.Max(1, request.MaxOutputTokens)));
            if (tools.Count > 0) root["tools"] = tools;
            var effort = Effort(profile, request);
            if (HasEffort(effort)) root["output_config"] = AgentJson.Object(("effort", effort));
            return root;
        }

        public override IAgentWireDecoder CreateDecoder(Action<AgentStreamEvent>? onEvent) =>
            new AnthropicMessagesDecoder(onEvent);

        private static Dictionary<string, object?> ParseToolInput(AgentToolCall call)
        {
            try
            {
                return AgentJson.ParseObject(call.ArgumentsJson);
            }
            catch (Exception exception) when (exception is FormatException or LitJson.JsonException)
            {
                throw new AgentProviderException(
                    $"Anthropic tool call '{call.Id}' contains invalid object arguments.", exception);
            }
        }

        internal static void ValidateThinkingBlock(Dictionary<string, object?> block)
        {
            var type = AgentJson.GetString(block, "type");
            if (type == "thinking")
            {
                if (!block.TryGetValue("thinking", out var thinking) || thinking is not string)
                    throw new AgentProviderException("Anthropic thinking block is missing its thinking text.");
                if (!block.TryGetValue("signature", out var signature) ||
                    signature is not string signatureText || string.IsNullOrWhiteSpace(signatureText))
                    throw new AgentProviderException("Anthropic thinking block is missing its signature.");
                return;
            }

            if (type == "redacted_thinking")
            {
                if (!block.TryGetValue("data", out var data) ||
                    data is not string dataText || string.IsNullOrWhiteSpace(dataText))
                    throw new AgentProviderException("Anthropic redacted thinking block is missing its data.");
                return;
            }

            throw new AgentProviderException(
                "Stored Anthropic provider data contains a non-thinking content block.");
        }
    }

    internal sealed class AnthropicMessagesDecoder : IAgentWireDecoder
    {
        private sealed class PendingCall
        {
            public string Id = string.Empty;
            public string Name = string.Empty;
            public string InitialArgumentsJson = "{}";
            public bool HasArgumentDeltas;
            public bool Started;
            public readonly StringBuilder Arguments = new();
        }

        private sealed class PendingThinkingBlock
        {
            public readonly Dictionary<string, object?> Block = new(StringComparer.Ordinal);
            public readonly StringBuilder Thinking = new();
            public readonly StringBuilder Signature = new();
            public bool HasThinkingDeltas;
            public bool HasSignatureDeltas;
            public bool SawStart;
            public bool Closed;
        }

        private readonly Action<AgentStreamEvent>? _onEvent;
        private readonly StringBuilder _text = new();
        private readonly SortedDictionary<int, PendingCall> _calls = new();
        private readonly SortedDictionary<int, PendingThinkingBlock> _thinkingBlocks = new();
        private AgentUsage _usage = new();
        private bool _terminal;
        private string _finishReason = string.Empty;

        public AnthropicMessagesDecoder(Action<AgentStreamEvent>? onEvent)
        {
            _onEvent = onEvent;
            _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.RunStarted));
        }

        public void Accept(SseEvent value)
        {
            if (string.IsNullOrWhiteSpace(value.Data)) return;
            var root = AgentJson.ParseObject(value.Data);
            var type = string.IsNullOrWhiteSpace(value.Name) ? AgentJson.GetString(root, "type") : value.Name;
            switch (type)
            {
                case "message_start":
                    if (AgentJson.GetObject(root, "message") is { } message &&
                        AgentJson.GetObject(message, "usage") is { } startUsage)
                        _usage.InputTokens = AgentJson.GetLong(startUsage, "input_tokens");
                    break;
                case "content_block_start":
                    ReadBlockStart(root);
                    break;
                case "content_block_delta":
                    ReadDelta(root);
                    break;
                case "content_block_stop":
                    ReadBlockStop(root);
                    break;
                case "message_delta":
                    if (AgentJson.GetObject(root, "delta") is { } delta)
                        _finishReason = AgentJson.GetString(delta, "stop_reason", _finishReason);
                    if (AgentJson.GetObject(root, "usage") is { } usage)
                    {
                        _usage.OutputTokens = AgentJson.GetLong(usage, "output_tokens", _usage.OutputTokens);
                        _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.UsageUpdated,
                            _usage.TotalTokens.ToString()));
                    }
                    break;
                case "message_stop":
                    _terminal = true;
                    break;
                case "error":
                {
                    var error = AgentJson.GetObject(root, "error");
                    var errorMessage = error == null
                        ? value.Data
                        : AgentJson.GetString(error, "message", value.Data);
                    throw Failure("Anthropic Messages request failed: " + errorMessage);
                }
            }
        }

        public AgentModelResponse Complete()
        {
            if (!_terminal)
                throw Failure("Anthropic Messages stream ended before message_stop.");
            if (string.IsNullOrWhiteSpace(_finishReason))
                throw Failure("Anthropic Messages stream ended without a stop reason.");
            if (_finishReason is "max_tokens" or "pause_turn" or "model_context_window_exceeded")
                throw Failure($"Anthropic Messages response stopped with stop_reason '{_finishReason}'.");
            var providerContent = new List<object?>();
            foreach (var pair in _thinkingBlocks)
            {
                var value = pair.Value;
                if (!value.SawStart || !value.Closed)
                    throw Failure($"Anthropic thinking block {pair.Key} did not complete.");
                if (AgentJson.GetString(value.Block, "type") == "thinking")
                {
                    value.Block["thinking"] = value.Thinking.ToString();
                    value.Block["signature"] = value.Signature.ToString();
                }
                try
                {
                    AnthropicMessagesWireProtocol.ValidateThinkingBlock(value.Block);
                }
                catch (AgentProviderException exception)
                {
                    throw Failure(exception.Message);
                }
                providerContent.Add(value.Block);
            }

            var response = new AgentModelResponse
            {
                Text = _text.ToString(),
                Usage = _usage,
                FinishReason = _finishReason,
                ProviderDataJson = AgentProviderDataEnvelope.Create(
                    AgentProtocolIds.AnthropicMessages, "content", providerContent)
            };
            foreach (var call in _calls.Values)
            {
                if (string.IsNullOrWhiteSpace(call.Id))
                    throw Failure("Anthropic Messages returned a tool_use block without an id.");
                if (string.IsNullOrWhiteSpace(call.Name))
                    throw Failure($"Anthropic tool_use '{call.Id}' is missing its name.");
                NotifyStarted(call);
                response.ToolCalls.Add(new AgentToolCall
                {
                    Id = call.Id,
                    Name = call.Name,
                    ArgumentsJson = call.HasArgumentDeltas && call.Arguments.Length > 0
                        ? call.Arguments.ToString()
                        : call.InitialArgumentsJson
                });
            }
            if (_finishReason == "tool_use" && response.ToolCalls.Count == 0)
                throw Failure("Anthropic Messages stopped for tool use but returned no complete tool_use block.");
            _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.RunCompleted));
            return response;
        }

        private void ReadBlockStart(Dictionary<string, object?> root)
        {
            var block = AgentJson.GetObject(root, "content_block");
            if (block == null) return;
            var index = EvalData.GetInt(root, "index");
            var type = AgentJson.GetString(block, "type");
            if (type is "thinking" or "redacted_thinking")
            {
                ReadThinkingBlockStart(index, block, type);
                return;
            }
            if (type != "tool_use") return;
            if (!_calls.TryGetValue(index, out var call))
            {
                call = new PendingCall();
                _calls.Add(index, call);
            }
            call.Id = AgentJson.GetString(block, "id", call.Id);
            call.Name = AgentJson.GetString(block, "name", call.Name);
            if (block.TryGetValue("input", out var input) && input != null)
                call.InitialArgumentsJson = AgentJson.Stringify(input);
            NotifyStarted(call);
        }

        private void ReadDelta(Dictionary<string, object?> root)
        {
            var delta = AgentJson.GetObject(root, "delta");
            if (delta == null) return;
            var type = AgentJson.GetString(delta, "type");
            if (type == "text_delta")
            {
                var text = AgentJson.GetString(delta, "text");
                _text.Append(text);
                _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.TextDelta, text));
                return;
            }
            if (type == "thinking_delta")
            {
                var thinking = AgentJson.GetString(delta, "thinking");
                var block = GetThinkingBlock(EvalData.GetInt(root, "index"));
                EnsureThinkingType(block, EvalData.GetInt(root, "index"));
                block.HasThinkingDeltas = true;
                block.Thinking.Append(thinking);
                _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.ReasoningDelta, thinking));
                return;
            }
            if (type == "signature_delta")
            {
                var block = GetThinkingBlock(EvalData.GetInt(root, "index"));
                EnsureThinkingType(block, EvalData.GetInt(root, "index"));
                block.HasSignatureDeltas = true;
                block.Signature.Append(AgentJson.GetString(delta, "signature"));
                return;
            }
            if (type != "input_json_delta") return;
            var index = EvalData.GetInt(root, "index");
            if (!_calls.TryGetValue(index, out var call))
            {
                call = new PendingCall();
                _calls.Add(index, call);
            }
            var arguments = AgentJson.GetString(delta, "partial_json");
            call.HasArgumentDeltas = true;
            call.Arguments.Append(arguments);
            _onEvent?.Invoke(new AgentStreamEvent(AgentStreamEventKind.ToolCallArgumentsDelta, arguments,
                call.Id));
        }

        private void ReadThinkingBlockStart(
            int index,
            Dictionary<string, object?> source,
            string type)
        {
            var block = GetThinkingBlock(index);
            var existingType = AgentJson.GetString(block.Block, "type");
            if (!string.IsNullOrEmpty(existingType) && existingType != type)
                throw Failure($"Anthropic content block {index} changed type while streaming.");
            foreach (var pair in source)
                block.Block[pair.Key] = pair.Value;
            block.SawStart = true;
            if (type != "thinking") return;
            if (!block.HasThinkingDeltas)
            {
                block.Thinking.Clear();
                block.Thinking.Append(AgentJson.GetString(source, "thinking"));
            }
            if (!block.HasSignatureDeltas)
            {
                block.Signature.Clear();
                block.Signature.Append(AgentJson.GetString(source, "signature"));
            }
        }

        private PendingThinkingBlock GetThinkingBlock(int index)
        {
            if (_thinkingBlocks.TryGetValue(index, out var block)) return block;
            block = new PendingThinkingBlock();
            _thinkingBlocks.Add(index, block);
            return block;
        }

        private void EnsureThinkingType(PendingThinkingBlock block, int index)
        {
            var type = AgentJson.GetString(block.Block, "type");
            if (string.IsNullOrEmpty(type))
                block.Block["type"] = "thinking";
            else if (type != "thinking")
                throw Failure($"Anthropic content block {index} received an invalid thinking delta.");
        }

        private void ReadBlockStop(Dictionary<string, object?> root)
        {
            var index = EvalData.GetInt(root, "index");
            if (_thinkingBlocks.TryGetValue(index, out var block)) block.Closed = true;
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
