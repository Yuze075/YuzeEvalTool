#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace YuzeToolkit.UnityAgent
{
    [Flags]
    public enum AgentModelCapabilities
    {
        None = 0,
        Text = 1 << 0,
        Streaming = 1 << 1,
        ToolUse = 1 << 2,
        Reasoning = 1 << 3,
        Vision = 1 << 4,
        StructuredOutput = 1 << 5
    }

    public enum AgentModelDiscoverySource
    {
        Remote = 0,
        CuratedFallback = 1,
        CuratedOnly = 2
    }

    public sealed class AgentModelPreset
    {
        internal AgentModelPreset(
            string id,
            string displayName,
            int contextTokens,
            int maximumOutputTokens,
            int recommendedOutputTokens,
            AgentModelCapabilities capabilities,
            IReadOnlyList<string>? reasoningEfforts = null,
            string defaultReasoningEffort = "",
            bool isPreview = false)
        {
            Id = id;
            DisplayName = displayName;
            ContextTokens = contextTokens;
            MaximumOutputTokens = maximumOutputTokens;
            RecommendedOutputTokens = recommendedOutputTokens;
            Capabilities = capabilities;
            ReasoningEfforts = reasoningEfforts ?? Array.Empty<string>();
            DefaultReasoningEffort = defaultReasoningEffort;
            IsPreview = isPreview;
        }

        public string Id { get; }

        public string DisplayName { get; }

        /// <summary>Zero means that the provider does not publish a stable limit.</summary>
        public int ContextTokens { get; }

        /// <summary>Zero means that the provider does not publish a stable limit.</summary>
        public int MaximumOutputTokens { get; }

        public int RecommendedOutputTokens { get; }

        public AgentModelCapabilities Capabilities { get; }

        public IReadOnlyList<string> ReasoningEfforts { get; }

        public string DefaultReasoningEffort { get; }

        public bool IsPreview { get; }
    }

    public sealed class AgentModelOption
    {
        internal AgentModelOption(string providerId, AgentModelPreset model, bool isRemote)
        {
            ProviderId = providerId;
            Id = model.Id;
            DisplayName = model.DisplayName;
            ContextTokens = model.ContextTokens;
            MaximumOutputTokens = model.MaximumOutputTokens;
            RecommendedOutputTokens = model.RecommendedOutputTokens;
            Capabilities = model.Capabilities;
            ReasoningEfforts = model.ReasoningEfforts;
            DefaultReasoningEffort = model.DefaultReasoningEffort;
            IsPreview = model.IsPreview;
            IsRemote = isRemote;
        }

        public string ProviderId { get; }

        public string Id { get; }

        public string DisplayName { get; }

        public int ContextTokens { get; }

        public int MaximumOutputTokens { get; }

        public int RecommendedOutputTokens { get; }

        public AgentModelCapabilities Capabilities { get; }

        public IReadOnlyList<string> ReasoningEfforts { get; }

        public string DefaultReasoningEffort { get; }

        public bool IsPreview { get; }

        public bool IsRemote { get; }
    }

    public sealed class AgentModelDiscoveryResult
    {
        internal AgentModelDiscoveryResult(
            IReadOnlyList<AgentModelOption> models,
            AgentModelDiscoverySource source,
            string warning)
        {
            Models = models;
            Source = source;
            Warning = warning;
        }

        public IReadOnlyList<AgentModelOption> Models { get; }

        public AgentModelDiscoverySource Source { get; }

        /// <summary>Populated when remote discovery failed and curated models were used.</summary>
        public string Warning { get; }
    }

    public sealed class AgentProviderPreset
    {
        internal AgentProviderPreset(
            string id,
            string displayName,
            string protocol,
            string baseUrl,
            string defaultModelId,
            IReadOnlyList<AgentModelPreset> models,
            string documentationUrl,
            bool supportsRemoteModelList,
            bool strictToolsByDefault,
            AgentWireCompatibility compatibility)
        {
            Id = id;
            DisplayName = displayName;
            Protocol = protocol;
            BaseUrl = baseUrl;
            DefaultModelId = defaultModelId;
            Models = models;
            DocumentationUrl = documentationUrl;
            SupportsRemoteModelList = supportsRemoteModelList;
            StrictToolsByDefault = strictToolsByDefault;
            Compatibility = compatibility;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public string Protocol { get; }

        public string BaseUrl { get; }

        public string DefaultModelId { get; }

        public IReadOnlyList<AgentModelPreset> Models { get; }

        public string DocumentationUrl { get; }

        public bool SupportsRemoteModelList { get; }

        public bool StrictToolsByDefault { get; }

        internal AgentWireCompatibility Compatibility { get; }
    }

    internal enum AgentChatTokenParameter
    {
        MaxTokens,
        MaxCompletionTokens
    }

    internal enum AgentChatReasoningStyle
    {
        ReasoningEffort,
        ThinkingToggle,
        ThinkingToggleAndEffort,
        AdaptiveThinkingToggle,
        None
    }

    internal sealed class AgentWireCompatibility
    {
        public AgentChatTokenParameter ChatTokenParameter { get; set; } = AgentChatTokenParameter.MaxTokens;

        public AgentChatReasoningStyle ChatReasoningStyle { get; set; } = AgentChatReasoningStyle.ReasoningEffort;

        public bool IncludeStreamOptions { get; set; }

        public bool IncludeParallelToolCalls { get; set; }

        public bool SupportsStrictTools { get; set; }

        public bool IncludeEncryptedReasoning { get; set; }

        public bool ResponsesReasoning { get; set; } = true;

        public bool IncludeReasoningSplit { get; set; }

        public static AgentWireCompatibility ConservativeCustom { get; } = new();
    }

    /// <summary>
    /// First-party provider presets and a deliberately small, maintained model catalog. The remote
    /// Models API remains authoritative; these entries provide safe defaults and offline fallback.
    /// </summary>
    public static class AgentProviderCatalog
    {
        private const AgentModelCapabilities AgentText = AgentModelCapabilities.Text |
                                                            AgentModelCapabilities.Streaming |
                                                            AgentModelCapabilities.ToolUse;
        private const AgentModelCapabilities ReasoningAgent = AgentText | AgentModelCapabilities.Reasoning;

        private static readonly string[] OpenAiEfforts = { "none", "low", "medium", "high", "xhigh", "max" };
        private static readonly string[] FiveEfforts = { "low", "medium", "high", "xhigh", "max" };
        private static readonly string[] GeminiEfforts = { "minimal", "low", "medium", "high" };
        private static readonly string[] XaiEfforts = { "low", "medium", "high", "xhigh" };
        private static readonly string[] MetaEfforts = { "minimal", "low", "medium", "high", "xhigh" };
        private static readonly string[] MiniMaxEfforts = { "disabled", "adaptive" };
        private static readonly string[] DeepSeekEfforts = { "disabled", "high", "max" };
        private static readonly string[] QwenEfforts =
            { "none", "minimal", "low", "medium", "high", "xhigh", "max" };
        private static readonly string[] MimoEfforts = { "none", "low", "medium", "high" };
        private static readonly string[] KimiCodeEfforts = { "low", "high", "max" };

        private static readonly AgentWireCompatibility OpenAiCompatibility = new()
        {
            ChatTokenParameter = AgentChatTokenParameter.MaxCompletionTokens,
            ChatReasoningStyle = AgentChatReasoningStyle.ReasoningEffort,
            IncludeStreamOptions = true,
            IncludeParallelToolCalls = true,
            SupportsStrictTools = true,
            IncludeEncryptedReasoning = true
        };

        private static readonly AgentWireCompatibility ResponsesCompatibility = new()
        {
            ChatReasoningStyle = AgentChatReasoningStyle.None,
            ResponsesReasoning = true
        };

        private static readonly AgentWireCompatibility MetaCompatibility = new()
        {
            ChatReasoningStyle = AgentChatReasoningStyle.None,
            IncludeEncryptedReasoning = true,
            ResponsesReasoning = true
        };

        private static readonly AgentWireCompatibility ToggleCompatibility = new()
        {
            ChatReasoningStyle = AgentChatReasoningStyle.ThinkingToggle
        };

        private static readonly AgentWireCompatibility ToggleAndEffortCompatibility = new()
        {
            ChatReasoningStyle = AgentChatReasoningStyle.ThinkingToggleAndEffort
        };

        private static readonly AgentWireCompatibility MiniMaxCompatibility = new()
        {
            ChatTokenParameter = AgentChatTokenParameter.MaxCompletionTokens,
            ChatReasoningStyle = AgentChatReasoningStyle.AdaptiveThinkingToggle,
            IncludeReasoningSplit = true
        };

        private static readonly IReadOnlyList<AgentProviderPreset> Values = CreateProviders();
        private static readonly Dictionary<string, AgentProviderPreset> ById =
            Values.ToDictionary(value => value.Id, StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<AgentProviderPreset> Providers => Values;

        public static AgentProviderPreset? FindProvider(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId)) return null;
            return ById.TryGetValue(providerId.Trim(), out var preset) ? preset : null;
        }

        public static AgentProviderPreset? FindProvider(AgentProviderProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            var byId = FindProvider(profile.ProviderPresetId);
            if (byId != null) return byId;
            var normalizedUrl = NormalizeUrl(profile.BaseUrl);
            return Values.FirstOrDefault(value =>
                string.Equals(value.Protocol, profile.Protocol, StringComparison.Ordinal) &&
                string.Equals(NormalizeUrl(value.BaseUrl), normalizedUrl, StringComparison.OrdinalIgnoreCase));
        }

        public static IReadOnlyList<AgentModelPreset> GetModels(string providerId) =>
            FindProvider(providerId)?.Models ?? Array.Empty<AgentModelPreset>();

        public static AgentModelPreset? GetModel(string providerId, string modelId) =>
            GetModels(providerId).FirstOrDefault(value =>
                string.Equals(value.Id, modelId, StringComparison.OrdinalIgnoreCase));

        public static IReadOnlyList<string> GetReasoningEfforts(string providerId, string modelId) =>
            GetModel(providerId, modelId)?.ReasoningEfforts ?? Array.Empty<string>();

        public static bool ApplyPreset(
            AgentProviderProfile target,
            string providerId,
            string? modelId = null)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            var provider = FindProvider(providerId);
            if (provider == null) return false;
            var model = string.IsNullOrWhiteSpace(modelId)
                ? GetModel(provider.Id, provider.DefaultModelId)
                : GetModel(provider.Id, modelId!);
            if (model == null && provider.Models.Count > 0) return false;

            target.ProviderPresetId = provider.Id;
            target.Name = provider.DisplayName;
            target.Protocol = provider.Protocol;
            target.BaseUrl = provider.BaseUrl;
            target.StrictTools = provider.StrictToolsByDefault;
            target.Model = model?.Id ?? string.Empty;
            target.ReasoningEffort = model?.DefaultReasoningEffort ?? string.Empty;
            target.MaxOutputTokens = Math.Max(1, model?.RecommendedOutputTokens ?? 4096);
            target.ContextWindowTokens = Math.Max(8_192, model?.ContextTokens ?? 128_000);
            return true;
        }

        internal static AgentWireCompatibility ResolveCompatibility(AgentProviderProfile profile) =>
            FindProvider(profile)?.Compatibility ?? AgentWireCompatibility.ConservativeCustom;

        internal static AgentModelDiscoveryResult CuratedResult(
            AgentProviderProfile profile,
            AgentModelDiscoverySource source,
            string warning = "")
        {
            var provider = FindProvider(profile);
            var models = provider == null
                ? Array.Empty<AgentModelOption>()
                : provider.Models.Select(value => new AgentModelOption(provider.Id, value, false)).ToArray();
            return new AgentModelDiscoveryResult(models, source, warning);
        }

        internal static AgentModelDiscoveryResult MergeRemoteModels(
            AgentProviderProfile profile,
            IReadOnlyList<string> remoteModelIds)
        {
            var provider = FindProvider(profile);
            var curated = provider?.Models.ToDictionary(value => value.Id, StringComparer.OrdinalIgnoreCase) ??
                          new Dictionary<string, AgentModelPreset>(StringComparer.OrdinalIgnoreCase);
            var providerId = provider?.Id ?? profile.ProviderPresetId ?? string.Empty;
            var options = remoteModelIds
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(id =>
                {
                    if (!curated.TryGetValue(id, out var model))
                        model = Model(id, id, 0, 0, Math.Max(1, profile.MaxOutputTokens), AgentText);
                    return new AgentModelOption(providerId, model, true);
                })
                .OrderByDescending(value => curated.ContainsKey(value.Id))
                .ThenBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new AgentModelDiscoveryResult(options, AgentModelDiscoverySource.Remote, string.Empty);
        }

        private static IReadOnlyList<AgentProviderPreset> CreateProviders()
        {
            return new[]
            {
                Provider("openai", "OpenAI", AgentProtocolIds.OpenAiResponses,
                    "https://api.openai.com/v1/", "gpt-5.6-sol",
                    "https://developers.openai.com/api/docs/models", true, true, OpenAiCompatibility,
                    Model("gpt-5.6-sol", "GPT-5.6 Sol", 1_050_000, 128_000, 32_768,
                        ReasoningAgent | AgentModelCapabilities.Vision | AgentModelCapabilities.StructuredOutput,
                        OpenAiEfforts, "medium"),
                    Model("gpt-5.6-terra", "GPT-5.6 Terra", 1_050_000, 128_000, 32_768,
                        ReasoningAgent | AgentModelCapabilities.Vision | AgentModelCapabilities.StructuredOutput,
                        OpenAiEfforts, "medium"),
                    Model("gpt-5.6-luna", "GPT-5.6 Luna", 1_050_000, 128_000, 32_768,
                        ReasoningAgent | AgentModelCapabilities.Vision | AgentModelCapabilities.StructuredOutput,
                        OpenAiEfforts, "medium")),

                Provider("anthropic", "Anthropic", AgentProtocolIds.AnthropicMessages,
                    "https://api.anthropic.com/v1/", "claude-opus-5",
                    "https://platform.claude.com/docs/en/about-claude/models/overview", true, false,
                    AgentWireCompatibility.ConservativeCustom,
                    Model("claude-opus-5", "Claude Opus 5", 1_000_000, 128_000, 65_536,
                        ReasoningAgent | AgentModelCapabilities.Vision, FiveEfforts, "high"),
                    Model("claude-fable-5", "Claude Fable 5", 1_000_000, 128_000, 65_536,
                        ReasoningAgent | AgentModelCapabilities.Vision, FiveEfforts, "high"),
                    Model("claude-sonnet-5", "Claude Sonnet 5", 1_000_000, 128_000, 32_768,
                        ReasoningAgent | AgentModelCapabilities.Vision, FiveEfforts, "high"),
                    Model("claude-haiku-4-5-20251001", "Claude Haiku 4.5", 200_000, 64_000, 16_384,
                        AgentText | AgentModelCapabilities.Vision)),

                Provider("google", "Google Gemini", AgentProtocolIds.GoogleGeminiInteractions,
                    "https://generativelanguage.googleapis.com/v1beta/",
                    "gemini-3.6-flash", "https://ai.google.dev/gemini-api/docs/latest-model", true, false,
                    AgentWireCompatibility.ConservativeCustom,
                    Model("gemini-3.6-flash", "Gemini 3.6 Flash", 1_048_576, 65_536, 32_768,
                        ReasoningAgent | AgentModelCapabilities.Vision | AgentModelCapabilities.StructuredOutput,
                        GeminiEfforts, "medium"),
                    Model("gemini-3.5-flash-lite", "Gemini 3.5 Flash-Lite", 1_048_576, 65_536, 16_384,
                        ReasoningAgent | AgentModelCapabilities.Vision | AgentModelCapabilities.StructuredOutput,
                        GeminiEfforts, "minimal"),
                    Model("gemini-3.5-flash", "Gemini 3.5 Flash", 1_048_576, 65_536, 32_768,
                        ReasoningAgent | AgentModelCapabilities.Vision | AgentModelCapabilities.StructuredOutput,
                        GeminiEfforts, "medium")),

                Provider("xai", "xAI", AgentProtocolIds.OpenAiResponses,
                    "https://api.x.ai/v1/", "grok-4.6",
                    "https://docs.x.ai/developers/models", true, false, ResponsesCompatibility,
                    Model("grok-4.6", "Grok 4.6", 500_000, 0, 32_768,
                        ReasoningAgent | AgentModelCapabilities.Vision | AgentModelCapabilities.StructuredOutput,
                        XaiEfforts, "high"),
                    Model("grok-4.5", "Grok 4.5", 500_000, 0, 32_768,
                        ReasoningAgent | AgentModelCapabilities.Vision | AgentModelCapabilities.StructuredOutput,
                        new[] { "low", "medium", "high" }, "high")),

                Provider("meta", "Meta Model API", AgentProtocolIds.OpenAiResponses,
                    "https://api.meta.ai/v1/", "muse-spark-1.1",
                    "https://ai.meta.com/blog/introducing-muse-spark-meta-model-api/", true, false,
                    MetaCompatibility,
                    Model("muse-spark-1.1", "Muse Spark 1.1 (Public Preview)", 1_048_576, 131_072, 32_768,
                        ReasoningAgent | AgentModelCapabilities.Vision | AgentModelCapabilities.StructuredOutput,
                        MetaEfforts, "high", true)),

                Provider("kimi", "Kimi / Moonshot AI", AgentProtocolIds.OpenAiChat,
                    "https://api.moonshot.ai/v1/", "kimi-k2.5",
                    "https://platform.moonshot.ai/docs", true, false,
                    AgentWireCompatibility.ConservativeCustom,
                    Model("kimi-k2.5", "Kimi K2.5", 262_144, 0, 32_768, ReasoningAgent)),

                Provider("kimi-code", "Kimi Code", AgentProtocolIds.OpenAiChat,
                    "https://api.kimi.com/coding/v1/", "k3",
                    "https://www.kimi.com/code/docs/en/", true, false,
                    AgentWireCompatibility.ConservativeCustom,
                    Model("k3", "Kimi K3", 1_048_576, 0, 32_768, ReasoningAgent,
                        KimiCodeEfforts, "high"),
                    Model("k3-256k", "Kimi K3 256K", 262_144, 0, 32_768, ReasoningAgent,
                        KimiCodeEfforts, "high"),
                    Model("kimi-for-coding", "Kimi K2.7 Code", 262_144, 0, 32_768, ReasoningAgent),
                    Model("kimi-for-coding-highspeed", "Kimi K2.7 Code HighSpeed", 262_144, 0, 32_768,
                        ReasoningAgent)),

                Provider("glm", "Z.AI / GLM", AgentProtocolIds.OpenAiChat,
                    "https://api.z.ai/api/paas/v4/", "glm-5.2",
                    "https://docs.z.ai/guides/llm/glm-5.2", true, false, ToggleAndEffortCompatibility,
                    Model("glm-5.2", "GLM-5.2", 1_000_000, 128_000, 32_768,
                        ReasoningAgent | AgentModelCapabilities.StructuredOutput, DeepSeekEfforts, "max"),
                    Model("glm-5.1", "GLM-5.1", 200_000, 128_000, 32_768,
                        ReasoningAgent | AgentModelCapabilities.StructuredOutput, DeepSeekEfforts, "high"),
                    Model("glm-5-turbo", "GLM-5-Turbo", 200_000, 128_000, 32_768,
                        ReasoningAgent | AgentModelCapabilities.StructuredOutput, DeepSeekEfforts, "high")),

                Provider("qwen", "Alibaba Qwen (International)", AgentProtocolIds.OpenAiResponses,
                    "https://dashscope-intl.aliyuncs.com/compatible-mode/v1/",
                    "qwen3.8-max", "https://help.aliyun.com/zh/model-studio/text-generation-model/", true,
                    false, ResponsesCompatibility,
                    Model("qwen3.8-max", "Qwen3.8 Max", 1_000_000, 0, 32_768,
                        ReasoningAgent | AgentModelCapabilities.StructuredOutput, QwenEfforts, "xhigh"),
                    Model("qwen3.7-plus", "Qwen3.7 Plus", 1_000_000, 0, 32_768,
                        ReasoningAgent | AgentModelCapabilities.StructuredOutput, QwenEfforts, "xhigh"),
                    Model("qwen3.7-flash", "Qwen3.7 Flash", 1_000_000, 0, 16_384,
                        ReasoningAgent | AgentModelCapabilities.StructuredOutput, QwenEfforts, "xhigh")),

                Provider("qwen-cn", "Alibaba Qwen (China)", AgentProtocolIds.OpenAiResponses,
                    "https://dashscope.aliyuncs.com/compatible-mode/v1/",
                    "qwen3.8-max", "https://help.aliyun.com/zh/model-studio/text-generation-model/", true,
                    false, ResponsesCompatibility,
                    Model("qwen3.8-max", "Qwen3.8 Max", 1_000_000, 0, 32_768,
                        ReasoningAgent | AgentModelCapabilities.StructuredOutput, QwenEfforts, "xhigh"),
                    Model("qwen3.7-plus", "Qwen3.7 Plus", 1_000_000, 0, 32_768,
                        ReasoningAgent | AgentModelCapabilities.StructuredOutput, QwenEfforts, "xhigh"),
                    Model("qwen3.7-flash", "Qwen3.7 Flash", 1_000_000, 0, 16_384,
                        ReasoningAgent | AgentModelCapabilities.StructuredOutput, QwenEfforts, "xhigh")),

                Provider("minimax", "MiniMax", AgentProtocolIds.OpenAiChat,
                    "https://api.minimax.io/v1/", "MiniMax-M3",
                    "https://www.minimax.io/models/text/m3", true, false, MiniMaxCompatibility,
                    Model("MiniMax-M3", "MiniMax M3", 1_000_000, 524_288, 131_072,
                        ReasoningAgent | AgentModelCapabilities.Vision, MiniMaxEfforts, "adaptive"),
                    Model("MiniMax-M2.7", "MiniMax M2.7", 204_800, 204_800, 65_536,
                        ReasoningAgent, new[] { "adaptive" }, "adaptive"),
                    Model("MiniMax-M2.7-highspeed", "MiniMax M2.7 Highspeed", 204_800, 204_800, 65_536,
                        ReasoningAgent, new[] { "adaptive" }, "adaptive")),

                Provider("mimo", "Xiaomi MiMo", AgentProtocolIds.OpenAiResponses,
                    "https://api.xiaomimimo.com/v1/", "mimo-v2.5-pro",
                    "https://mimo.mi.com/docs/en-US/api/chat/responses", true, false, ResponsesCompatibility,
                    Model("mimo-v2.5-pro", "MiMo V2.5 Pro", 1_000_000, 131_072, 32_768,
                        ReasoningAgent | AgentModelCapabilities.StructuredOutput, MimoEfforts, "high"),
                    Model("mimo-v2.5", "MiMo V2.5", 1_000_000, 131_072, 32_768,
                        ReasoningAgent | AgentModelCapabilities.Vision | AgentModelCapabilities.StructuredOutput,
                        MimoEfforts, "high")),

                Provider("deepseek", "DeepSeek", AgentProtocolIds.OpenAiChat,
                    "https://api.deepseek.com/", "deepseek-v4-pro",
                    "https://api-docs.deepseek.com/quick_start/pricing", true, false,
                    ToggleAndEffortCompatibility,
                    Model("deepseek-v4-pro", "DeepSeek V4 Pro", 1_000_000, 384_000, 32_768,
                        ReasoningAgent, DeepSeekEfforts, "high"),
                    Model("deepseek-v4-flash", "DeepSeek V4 Flash", 1_000_000, 384_000, 32_768,
                        ReasoningAgent, DeepSeekEfforts, "high"))
            };
        }

        private static AgentProviderPreset Provider(
            string id,
            string displayName,
            string protocol,
            string baseUrl,
            string defaultModelId,
            string documentationUrl,
            bool supportsRemoteModelList,
            bool strictToolsByDefault,
            AgentWireCompatibility compatibility,
            params AgentModelPreset[] models) =>
            new(id, displayName, protocol, baseUrl, defaultModelId, models,
                documentationUrl, supportsRemoteModelList, strictToolsByDefault, compatibility);

        private static AgentModelPreset Model(
            string id,
            string displayName,
            int contextTokens,
            int maximumOutputTokens,
            int recommendedOutputTokens,
            AgentModelCapabilities capabilities,
            IReadOnlyList<string>? reasoningEfforts = null,
            string defaultReasoningEffort = "",
            bool isPreview = false) =>
            new(id, displayName, contextTokens, maximumOutputTokens, recommendedOutputTokens, capabilities,
                reasoningEfforts, defaultReasoningEffort, isPreview);

        private static string NormalizeUrl(string value) =>
            string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().TrimEnd('/');
    }
}
