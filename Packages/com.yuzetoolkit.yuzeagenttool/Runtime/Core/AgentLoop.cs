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
    internal sealed class AgentLoop
    {
        private const int MaximumMessageTextCharacters = 1_000_000;
        private const int MaximumProviderDataCharacters = 4_000_000;
        private const int MaximumToolArgumentsCharacters = 1_000_000;
        private const int MaximumSummaryCharacters = 200_000;
        private const int SummaryOutputTokens = 2_048;
        private const int ContextSafetyTokens = 2_048;
        private const int MinimumInputBudgetTokens = 4_096;
        private const double ContextUseRatio = 0.9;
        private const double RetainedTailRatio = 0.45;
        private const double SummaryChunkRatio = 0.4;
        private readonly AgentToolRegistry _tools;
        private readonly AgentApprovalService _approvals;
        private readonly AgentInstructionService _instructions;
        private readonly IAgentModelProvider _provider;

        public AgentLoop(AgentToolRegistry tools, AgentApprovalService approvals,
            AgentInstructionService instructions, IAgentModelProvider provider)
        {
            _tools = tools;
            _approvals = approvals;
            _instructions = instructions;
            _provider = provider;
        }

        public async Task RunAsync(AgentSessionRuntime runtime, AgentSettingsDocument settings,
            AgentProviderProfile profile, Func<Task> save, Action changed, Action<AgentStreamEvent>? onStreamEvent,
            CancellationToken cancellationToken)
        {
            var instructions = await _instructions.LoadAsync(settings, runtime.Document.WorkingDirectory,
                cancellationToken).ConfigureAwait(false);
            var systemPrompt = (AgentPaths.IsEditor ? settings.EditorSystemPrompt : settings.RuntimeSystemPrompt) +
                               instructions.Prompt;
            var maximumSteps = Math.Max(1, settings.MaximumAgentSteps);
            for (var step = 0; step < maximumSteps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (runtime.SyncRoot)
                {
                    runtime.Document.State = AgentSessionState.Running;
                    runtime.Document.CompletedSteps = step;
                    runtime.Document.PendingApproval = null;
                    runtime.LiveText = string.Empty;
                    runtime.LiveReasoning = string.Empty;
                    runtime.Document.UpdatedAtUtc = DateTime.UtcNow;
                }
                changed();
                await save().ConfigureAwait(false);

                await CompactContextIfNeededAsync(runtime, profile, systemPrompt,
                    settings.DefaultToolTimeoutSeconds, save, changed, cancellationToken).ConfigureAwait(false);
                var request = BuildRequest(runtime, profile, systemPrompt, settings.DefaultToolTimeoutSeconds);
                var response = await _provider.CompleteAsync(profile, request, value =>
                {
                    lock (runtime.SyncRoot)
                    {
                        if (value.Kind == AgentStreamEventKind.TextDelta)
                            runtime.LiveText += value.Text;
                        else if (value.Kind == AgentStreamEventKind.ReasoningDelta)
                            runtime.LiveReasoning += value.Text;
                    }
                    changed();
                    onStreamEvent?.Invoke(value);
                }, cancellationToken).ConfigureAwait(false);
                ValidateProviderResponse(response);

                lock (runtime.SyncRoot)
                {
                    runtime.LiveText = string.Empty;
                    runtime.LiveReasoning = string.Empty;
                    runtime.Document.Messages.Add(new AgentMessage
                    {
                        Role = AgentMessageRole.Assistant,
                        Text = response.Text,
                        ToolCalls = response.ToolCalls,
                        ProviderDataJson = response.ProviderDataJson
                    });
                    if (!string.IsNullOrWhiteSpace(response.ProviderThreadId))
                        runtime.Document.ProviderThreadId = response.ProviderThreadId;
                    AddUsage(runtime.Document.Usage, response.Usage);
                    runtime.Document.UpdatedAtUtc = DateTime.UtcNow;
                }
                changed();
                await save().ConfigureAwait(false);

                if (response.ToolCalls.Count == 0)
                {
                    lock (runtime.SyncRoot)
                    {
                        runtime.Document.State = AgentSessionState.Completed;
                        runtime.Document.CompletedSteps = step + 1;
                        runtime.Document.LastError = string.Empty;
                    }
                    changed();
                    await save().ConfigureAwait(false);
                    return;
                }

                foreach (var call in response.ToolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    onStreamEvent?.Invoke(new AgentStreamEvent(
                        AgentStreamEventKind.ToolExecutionStarted, call.Name, call.Id));
                    AgentToolResult result;
                    try
                    {
                        result = await ExecuteToolAsync(runtime, call, settings.DefaultToolTimeoutSeconds, save,
                                changed, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        onStreamEvent?.Invoke(new AgentStreamEvent(
                            AgentStreamEventKind.ToolExecutionCompleted,
                            "Tool execution was canceled.", call.Id, true));
                        throw;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    result = BoundToolResult(result);
                    onStreamEvent?.Invoke(new AgentStreamEvent(
                        AgentStreamEventKind.ToolExecutionCompleted, result.Text, call.Id, result.IsError));
                    lock (runtime.SyncRoot)
                    {
                        runtime.Document.Messages.Add(new AgentMessage
                        {
                            Role = AgentMessageRole.Tool,
                            ToolCallId = call.Id,
                            ToolName = call.Name,
                            Text = result.Text,
                            IsError = result.IsError
                        });
                        runtime.Document.State = AgentSessionState.Running;
                        runtime.Document.PendingApproval = null;
                        runtime.Document.UpdatedAtUtc = DateTime.UtcNow;
                    }
                    changed();
                    await save().ConfigureAwait(false);
                }
            }

            lock (runtime.SyncRoot)
            {
                runtime.Document.State = AgentSessionState.StepLimitReached;
                runtime.Document.CompletedSteps = maximumSteps;
                runtime.Document.PendingApproval = null;
                runtime.Document.LastError =
                    $"Agent turn reached the configured limit of {maximumSteps} model steps.";
                runtime.Document.UpdatedAtUtc = DateTime.UtcNow;
            }
            changed();
            await save().ConfigureAwait(false);
        }

        private AgentModelRequest BuildRequest(AgentSessionRuntime runtime, AgentProviderProfile profile,
            string systemPrompt, int defaultToolTimeoutSeconds)
        {
            List<AgentMessage> messages;
            string model;
            string effort;
            AgentPermissionMode permissionMode;
            lock (runtime.SyncRoot)
            {
                messages = ProjectMessages(runtime.Document);
                model = string.IsNullOrWhiteSpace(runtime.Document.Model) ? profile.Model : runtime.Document.Model;
                effort = string.IsNullOrWhiteSpace(runtime.Document.ReasoningEffort)
                    ? profile.ReasoningEffort
                    : runtime.Document.ReasoningEffort;
                permissionMode = runtime.Document.PermissionMode;
            }
            var surface = AgentToolPolicy.CurrentSurface;
            return new AgentModelRequest
            {
                SessionId = runtime.Document.Id,
                ProviderThreadId = runtime.Document.ProviderThreadId,
                WorkingDirectory = runtime.Document.WorkingDirectory,
                PermissionMode = permissionMode,
                DefaultToolTimeoutSeconds = Math.Max(1, defaultToolTimeoutSeconds),
                SystemPrompt = systemPrompt,
                Model = model,
                ReasoningEffort = effort,
                MaxOutputTokens = Math.Max(1, profile.MaxOutputTokens),
                Messages = messages,
                Tools = _tools.ListDescriptors(permissionMode, surface)
            };
        }

        private async Task<AgentToolResult> ExecuteToolAsync(AgentSessionRuntime runtime, AgentToolCall call,
            int defaultToolTimeoutSeconds, Func<Task> save, Action changed,
            CancellationToken cancellationToken)
        {
            if (!_tools.TryGet(call.Name, out var tool))
                return AgentToolResult.Error($"Unknown Agent Tool '{call.Name}'.");
            Dictionary<string, object?> arguments;
            try
            {
                arguments = AgentToolArguments.Parse(call.ArgumentsJson);
            }
            catch (ArgumentException exception)
            {
                return AgentToolResult.Error(exception.Message);
            }

            AgentPermissionMode permissionMode;
            lock (runtime.SyncRoot) permissionMode = runtime.Document.PermissionMode;
            var surface = AgentToolPolicy.CurrentSurface;
            if (!AgentToolPolicy.IsExposed(tool.Descriptor, permissionMode, surface))
                return AgentToolResult.Error(
                    $"Agent Tool '{call.Name}' is not available in {permissionMode} mode on {surface}.");
            if (AgentToolPolicy.RequiresApproval(tool.Descriptor, permissionMode))
            {
                var approval = new AgentApprovalRequest
                {
                    SessionId = runtime.Document.Id,
                    ToolCallId = call.Id,
                    ToolName = call.Name,
                    ArgumentsJson = call.ArgumentsJson,
                    Description = $"{tool.Descriptor.Risk}: {tool.Descriptor.Description}"
                };
                lock (runtime.SyncRoot)
                {
                    runtime.Document.State = AgentSessionState.AwaitingApproval;
                    runtime.Document.PendingApproval = approval;
                    runtime.Document.UpdatedAtUtc = DateTime.UtcNow;
                }
                var approved = await WaitForApprovalAsync(approval, save, changed, cancellationToken)
                    .ConfigureAwait(false);
                if (!approved) return AgentToolResult.Error($"User declined '{call.Name}'.");
            }

            lock (runtime.SyncRoot)
            {
                runtime.Document.State = AgentSessionState.Running;
                runtime.Document.PendingApproval = null;
            }
            changed();
            await save().ConfigureAwait(false);
            try
            {
                return await tool.ExecuteAsync(
                    new AgentToolContext(runtime.Document.Id, runtime.Document.WorkingDirectory,
                        defaultToolTimeoutSeconds, permissionMode, surface), arguments, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return AgentToolResult.Error($"{exception.GetType().Name}: {exception.Message}");
            }
        }

        private Task<bool> WaitForApprovalAsync(AgentApprovalRequest approval, Func<Task> save, Action changed,
            CancellationToken cancellationToken)
        {
            return _approvals.WaitForDecisionAsync(approval, cancellationToken, async () =>
            {
                changed();
                await save().ConfigureAwait(false);
            });
        }

        private async Task CompactContextIfNeededAsync(AgentSessionRuntime runtime, AgentProviderProfile profile,
            string systemPrompt, int defaultToolTimeoutSeconds, Func<Task> save, Action changed,
            CancellationToken cancellationToken)
        {
            for (var pass = 0; pass < 32; pass++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CompactionSnapshot snapshot;
                AgentPermissionMode permissionMode;
                lock (runtime.SyncRoot)
                    permissionMode = runtime.Document.PermissionMode;
                var tools = _tools.ListDescriptors(permissionMode, AgentToolPolicy.CurrentSurface);
                lock (runtime.SyncRoot)
                    snapshot = CaptureCompaction(runtime.Document, profile, systemPrompt, tools);
                if (!snapshot.RequiresCompaction) return;
                if (snapshot.ThroughMessageCount <= snapshot.PreviousMessageCount)
                    throw new InvalidDataException(
                        "The active Yuze Agent Tool conversation boundary exceeds the model context window.");

                var summaryRequest = new AgentModelRequest
                {
                    SessionId = runtime.Document.Id + "-context-summary",
                    WorkingDirectory = runtime.Document.WorkingDirectory,
                    PermissionMode = AgentPermissionMode.FullAccess,
                    DefaultToolTimeoutSeconds = Math.Max(1, defaultToolTimeoutSeconds),
                    SystemPrompt =
                        "Summarize the supplied earlier Yuze Agent Tool conversation for continuation. Preserve the user's " +
                        "objective, decisions, relevant files and Unity objects, exact tool evidence and errors, completed " +
                        "work, and unfinished next steps. Resolve no new task and return only the compact factual summary.",
                    Model = string.IsNullOrWhiteSpace(runtime.Document.Model) ? profile.Model : runtime.Document.Model,
                    ReasoningEffort = string.IsNullOrWhiteSpace(runtime.Document.ReasoningEffort)
                        ? profile.ReasoningEffort
                        : runtime.Document.ReasoningEffort,
                    MaxOutputTokens = Math.Min(SummaryOutputTokens, Math.Max(256, profile.MaxOutputTokens)),
                    Messages = new[]
                    {
                        new AgentMessage { Role = AgentMessageRole.User, Text = BuildSummaryInput(snapshot) }
                    },
                    Tools = Array.Empty<AgentToolDescriptor>()
                };
                var response = await _provider.CompleteAsync(profile, summaryRequest, null, cancellationToken)
                    .ConfigureAwait(false);
                ValidateProviderResponse(response);
                if (response.ToolCalls.Count > 0)
                    throw new InvalidDataException("Context compaction returned an unexpected tool call.");
                if (string.IsNullOrWhiteSpace(response.Text))
                    throw new InvalidDataException("Context compaction returned an empty summary.");

                lock (runtime.SyncRoot)
                {
                    if (runtime.Document.ContextSummaryMessageCount != snapshot.PreviousMessageCount ||
                        runtime.Document.Messages.Count < snapshot.ThroughMessageCount ||
                        !string.Equals(runtime.Document.Messages[snapshot.ThroughMessageCount - 1].Id,
                            snapshot.LastMessageId, StringComparison.Ordinal))
                        throw new InvalidOperationException("Conversation changed while its context was being compacted.");
                    runtime.Document.Summary = BoundSummary(response.Text.Trim());
                    runtime.Document.ContextSummaryMessageCount = snapshot.ThroughMessageCount;
                    AddUsage(runtime.Document.Usage, response.Usage);
                    runtime.Document.UpdatedAtUtc = DateTime.UtcNow;
                }
                changed();
                await save().ConfigureAwait(false);
            }
            throw new InvalidDataException("Context compaction did not converge within 32 summary passes.");
        }

        private static CompactionSnapshot CaptureCompaction(AgentSessionDocument document,
            AgentProviderProfile profile, string systemPrompt, IReadOnlyList<AgentToolDescriptor> tools)
        {
            var activeModel = string.IsNullOrWhiteSpace(document.Model) ? profile.Model : document.Model;
            var inputBudget = ResolveInputTokenBudget(profile, activeModel, systemPrompt, tools);
            if (ProjectMessages(document).Sum(EstimateTokens) <= inputBudget)
                return CompactionSnapshot.NotRequired;

            var previous = Math.Min(document.ContextSummaryMessageCount, document.Messages.Count);
            var retainedBudget = Math.Max(1_024, (int)(inputBudget * RetainedTailRatio));
            var retainStart = document.Messages.Count;
            var retainedTokens = 0;
            for (var index = document.Messages.Count - 1; index >= previous; index--)
            {
                var next = retainedTokens + EstimateTokens(document.Messages[index]);
                if (next > retainedBudget && retainStart < document.Messages.Count) break;
                retainedTokens = next;
                retainStart = index;
            }
            retainStart = MoveToConversationBoundary(document.Messages, retainStart);
            retainStart = Math.Min(retainStart, Math.Max(previous, document.Messages.Count - 1));

            var chunkBudget = Math.Max(1_024, (int)(inputBudget * SummaryChunkRatio));
            var through = previous;
            var chunkTokens = EstimateTokens(document.Summary);
            for (var index = previous; index < retainStart; index++)
            {
                var next = chunkTokens + EstimateTokens(document.Messages[index]);
                if (next > chunkBudget && through > previous) break;
                chunkTokens = next;
                through = index + 1;
            }
            through = MoveToConversationBoundary(document.Messages, through);
            if (through <= previous && retainStart > previous) through = retainStart;
            through = Math.Min(through, Math.Max(previous, document.Messages.Count - 1));
            return through <= previous
                ? new CompactionSnapshot(true, previous, through, document.Summary,
                    Array.Empty<AgentMessage>(), string.Empty)
                : new CompactionSnapshot(true, previous, through, document.Summary,
                    document.Messages.Skip(previous).Take(through - previous)
                        .Select(CloneMessageForContext).ToList(), document.Messages[through - 1].Id);
        }

        private static int ResolveInputTokenBudget(AgentProviderProfile profile, string activeModel,
            string systemPrompt, IReadOnlyList<AgentToolDescriptor> tools)
        {
            var model = AgentProviderCatalog.GetModel(profile.ProviderPresetId, activeModel);
            var context = Math.Max(8_192,
                model is { ContextTokens: > 0 } ? model.ContextTokens : profile.ContextWindowTokens);
            var available = (int)(context * ContextUseRatio) - Math.Max(1, profile.MaxOutputTokens) -
                            ContextSafetyTokens - EstimateTokens(systemPrompt);
            foreach (var tool in tools)
                available -= EstimateTokens(tool.Name) + EstimateTokens(tool.Description) +
                             EstimateTokens(AgentJson.Stringify(tool.Parameters));
            if (available < MinimumInputBudgetTokens)
                throw new InvalidDataException(
                    "The selected model context window is too small for the system prompt, tool schemas, and output reserve.");
            return available;
        }

        private static string BuildSummaryInput(CompactionSnapshot snapshot)
        {
            var text = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(snapshot.PreviousSummary))
            {
                text.AppendLine("<previous_summary>");
                text.AppendLine(snapshot.PreviousSummary);
                text.AppendLine("</previous_summary>");
            }
            text.AppendLine("<conversation_chunk>");
            foreach (var message in snapshot.Messages)
            {
                text.Append('[').Append(message.Role).Append("] ").AppendLine(message.Text);
                foreach (var call in message.ToolCalls)
                    text.Append("[ToolCall id=").Append(call.Id).Append(" name=").Append(call.Name)
                        .Append("] ").AppendLine(call.ArgumentsJson);
                if (message.Role == AgentMessageRole.Tool)
                    text.Append("[ToolResult id=").Append(message.ToolCallId).Append(" error=")
                        .Append(message.IsError).AppendLine("]");
            }
            text.Append("</conversation_chunk>");
            return text.ToString();
        }

        private static List<AgentMessage> ProjectMessages(AgentSessionDocument document)
        {
            var start = Math.Min(document.ContextSummaryMessageCount, document.Messages.Count);
            var messages = new List<AgentMessage>(document.Messages.Count - start + 1);
            if (!string.IsNullOrWhiteSpace(document.Summary))
                messages.Add(new AgentMessage
                {
                    Role = AgentMessageRole.User,
                    Text = "[UnityAgentTool context checkpoint]\n" + document.Summary
                });
            messages.AddRange(document.Messages.Skip(start).Select(CloneMessageForContext));
            return messages;
        }

        private static AgentMessage CloneMessageForContext(AgentMessage source) => new()
        {
            Id = source.Id,
            Role = source.Role,
            Text = source.Text,
            ToolCalls = source.ToolCalls.Select(call => new AgentToolCall
            {
                Id = call.Id,
                Name = call.Name,
                ArgumentsJson = call.ArgumentsJson,
                ProviderItemId = call.ProviderItemId
            }).ToList(),
            ToolCallId = source.ToolCallId,
            ToolName = source.ToolName,
            IsError = source.IsError,
            ProviderDataJson = source.ProviderDataJson,
            CreatedAtUtc = source.CreatedAtUtc
        };

        private static int MoveToConversationBoundary(IReadOnlyList<AgentMessage> messages, int candidate)
        {
            candidate = Math.Max(0, Math.Min(candidate, messages.Count));
            while (candidate > 0 && candidate < messages.Count && messages[candidate].Role == AgentMessageRole.Tool)
                candidate--;
            return candidate;
        }

        private static void ValidateProviderResponse(AgentModelResponse response)
        {
            if (response == null) throw new InvalidDataException("Model provider returned no response.");
            response.Text ??= string.Empty;
            response.ProviderDataJson ??= string.Empty;
            response.Usage ??= new AgentUsage();
            if (response.Text.Length > MaximumMessageTextCharacters)
                throw new InvalidDataException(
                    $"Model response text exceeds {MaximumMessageTextCharacters:N0} characters.");
            if (response.ProviderDataJson.Length > MaximumProviderDataCharacters)
                throw new InvalidDataException(
                    $"Model provider state exceeds {MaximumProviderDataCharacters:N0} characters.");
            if (response.ToolCalls == null)
                throw new InvalidDataException("Model provider returned a null tool-call collection.");
            var callIds = new HashSet<string>(StringComparer.Ordinal);
            long totalArguments = 0;
            foreach (var call in response.ToolCalls)
            {
                if (call == null) throw new InvalidDataException("Model provider returned a null tool call.");
                call.Id ??= string.Empty;
                call.Name ??= string.Empty;
                call.ArgumentsJson ??= "{}";
                call.ProviderItemId ??= string.Empty;
                if (string.IsNullOrWhiteSpace(call.Id))
                    throw new InvalidDataException("Model provider returned a tool call without an id.");
                if (!callIds.Add(call.Id))
                    throw new InvalidDataException($"Model provider returned duplicate tool-call id '{call.Id}'.");
                if (string.IsNullOrWhiteSpace(call.Name))
                    throw new InvalidDataException($"Model provider returned tool call '{call.Id}' without a name.");
                if (call.ArgumentsJson.Length > MaximumToolArgumentsCharacters)
                    throw new InvalidDataException(
                        $"Tool call '{call.Name}' arguments exceed {MaximumToolArgumentsCharacters:N0} characters.");
                totalArguments += call.ArgumentsJson.Length;
                if (totalArguments > MaximumProviderDataCharacters)
                    throw new InvalidDataException(
                        $"Model tool-call arguments exceed {MaximumProviderDataCharacters:N0} characters in one response.");
            }
        }

        private static AgentToolResult BoundToolResult(AgentToolResult result)
        {
            if (result == null) return AgentToolResult.Error("Agent Tool returned no result.");
            result.Text ??= string.Empty;
            if (result.Text.Length <= MaximumMessageTextCharacters) return result;
            return new AgentToolResult
            {
                IsError = result.IsError,
                Text = ElideMiddle(result.Text, MaximumMessageTextCharacters,
                    "\n… UnityAgentTool truncated this tool result for bounded conversation storage …\n")
            };
        }

        private static string BoundSummary(string summary) => summary.Length <= MaximumSummaryCharacters
            ? summary
            : ElideMiddle(summary, MaximumSummaryCharacters,
                "\n… older context summary content omitted to keep the checkpoint bounded …\n");

        private static int EstimateTokens(AgentMessage message)
        {
            long result = EstimateTokens(message.Text) + EstimateTokens(message.ProviderDataJson) + 64;
            foreach (var call in message.ToolCalls)
                result += EstimateTokens(call.Id) + EstimateTokens(call.Name) + EstimateTokens(call.ArgumentsJson) + 32;
            return (int)Math.Min(int.MaxValue, result);
        }

        private static int EstimateTokens(string? value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            long weightedCharacters = 0;
            foreach (var character in value) weightedCharacters += character <= 0x7f ? 1 : 3;
            return (int)Math.Min(int.MaxValue, (weightedCharacters + 3) / 4);
        }

        private static void AddUsage(AgentUsage target, AgentUsage source)
        {
            target.InputTokens = SaturatingAdd(target.InputTokens, source.InputTokens);
            target.OutputTokens = SaturatingAdd(target.OutputTokens, source.OutputTokens);
        }

        private static long SaturatingAdd(long left, long right)
        {
            if (right > 0 && left > long.MaxValue - right) return long.MaxValue;
            if (right < 0 && left < long.MinValue - right) return long.MinValue;
            return left + right;
        }

        private static string ElideMiddle(string value, int maximumCharacters, string marker)
        {
            if (maximumCharacters <= 0) return string.Empty;
            if (value.Length <= maximumCharacters) return value;
            if (marker.Length >= maximumCharacters) return marker.Substring(0, maximumCharacters);
            var available = maximumCharacters - marker.Length;
            var head = available / 2;
            var tail = available - head;
            return value.Substring(0, head) + marker + value.Substring(value.Length - tail, tail);
        }

        private readonly struct CompactionSnapshot
        {
            public static CompactionSnapshot NotRequired { get; } =
                new(false, 0, 0, string.Empty, Array.Empty<AgentMessage>(), string.Empty);

            public CompactionSnapshot(bool requiresCompaction, int previousMessageCount,
                int throughMessageCount, string previousSummary, IReadOnlyList<AgentMessage> messages,
                string lastMessageId)
            {
                RequiresCompaction = requiresCompaction;
                PreviousMessageCount = previousMessageCount;
                ThroughMessageCount = throughMessageCount;
                PreviousSummary = previousSummary;
                Messages = messages;
                LastMessageId = lastMessageId;
            }

            public bool RequiresCompaction { get; }
            public int PreviousMessageCount { get; }
            public int ThroughMessageCount { get; }
            public string PreviousSummary { get; }
            public IReadOnlyList<AgentMessage> Messages { get; }
            public string LastMessageId { get; }
        }
    }

    internal static class AgentConversationIntegrity
    {
        public static int CloseIncompleteToolCalls(AgentSessionDocument document, string reason)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var totalInserted = 0;
            for (var assistantIndex = 0; assistantIndex < document.Messages.Count; assistantIndex++)
            {
                var assistant = document.Messages[assistantIndex];
                if (assistant.Role != AgentMessageRole.Assistant || assistant.ToolCalls.Count == 0) continue;
                var resultEnd = assistantIndex + 1;
                while (resultEnd < document.Messages.Count &&
                       document.Messages[resultEnd].Role == AgentMessageRole.Tool)
                    resultEnd++;
                var available = document.Messages.GetRange(assistantIndex + 1, resultEnd - assistantIndex - 1);
                var consumed = new bool[available.Count];
                var ordered = new List<AgentMessage>(assistant.ToolCalls.Count + available.Count);
                var insertedForBatch = 0;
                foreach (var call in assistant.ToolCalls)
                {
                    var match = -1;
                    for (var index = 0; index < available.Count; index++)
                    {
                        if (!consumed[index] &&
                            string.Equals(available[index].ToolCallId, call.Id, StringComparison.Ordinal))
                        {
                            match = index;
                            break;
                        }
                    }
                    if (match >= 0)
                    {
                        consumed[match] = true;
                        ordered.Add(available[match]);
                    }
                    else
                    {
                        ordered.Add(new AgentMessage
                        {
                            Role = AgentMessageRole.Tool,
                            ToolCallId = call.Id,
                            ToolName = call.Name,
                            Text = reason,
                            IsError = true
                        });
                        insertedForBatch++;
                    }
                }
                for (var index = 0; index < available.Count; index++)
                    if (!consumed[index]) ordered.Add(available[index]);
                var reordered = available.Count != ordered.Count ||
                                !available.SequenceEqual(ordered);
                if (reordered)
                {
                    document.Messages.RemoveRange(assistantIndex + 1, available.Count);
                    document.Messages.InsertRange(assistantIndex + 1, ordered);
                    if (assistantIndex + 1 < document.ContextSummaryMessageCount && insertedForBatch > 0)
                    {
                        // The old checkpoint cannot describe a newly inserted cancellation result.
                        // Full history is still present, so invalidate it and let the next turn rebuild it.
                        document.Summary = string.Empty;
                        document.ContextSummaryMessageCount = 0;
                    }
                }
                totalInserted += insertedForBatch;
                assistantIndex += ordered.Count;
            }
            if (totalInserted > 0) document.UpdatedAtUtc = DateTime.UtcNow;
            return totalInserted;
        }
    }

    internal sealed class AgentSessionRuntime : IDisposable
    {
        public AgentSessionRuntime(AgentSessionDocument document) => Document = document;
        public object SyncRoot { get; } = new();
        public AgentSessionDocument Document { get; }
        public SemaphoreSlim TurnGate { get; } = new(1, 1);
        public CancellationTokenSource? ActiveCancellation { get; set; }
        public string InterruptionMessage { get; set; } = string.Empty;
        public bool IsDeleting { get; set; }
        public string LiveText { get; set; } = string.Empty;
        public string LiveReasoning { get; set; } = string.Empty;

        public void Dispose()
        {
            ActiveCancellation?.Cancel();
            ActiveCancellation?.Dispose();
            TurnGate.Dispose();
        }
    }
}
