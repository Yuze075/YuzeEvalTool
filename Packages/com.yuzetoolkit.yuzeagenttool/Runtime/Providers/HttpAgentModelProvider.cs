#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace YuzeToolkit.UnityAgent
{
    public sealed class HttpAgentModelProvider : IAgentModelProvider, IDisposable
    {
        private const int MaximumTurnAttempts = 3;
        private readonly AgentProviderTransport _transport;

        public HttpAgentModelProvider()
        {
            _transport = new AgentProviderTransport();
        }

        internal HttpAgentModelProvider(HttpClient client)
        {
            _transport = new AgentProviderTransport(client ?? throw new ArgumentNullException(nameof(client)));
        }

        public async Task<AgentModelResponse> CompleteAsync(
            AgentProviderProfile profile,
            AgentModelRequest request,
            Action<AgentStreamEvent>? onEvent,
            CancellationToken cancellationToken)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (request == null) throw new ArgumentNullException(nameof(request));
            var profileSnapshot = Snapshot(profile);
            var protocol = AgentWireProtocolFactory.Create(profileSnapshot.Protocol);
            var secret = profileSnapshot.ApiKey;
            var uri = ResolveUri(profileSnapshot.BaseUrl, protocol.TurnPath);
            var json = AgentJson.Stringify(protocol.CreateRequest(profileSnapshot, request));
            var headers = protocol.CreateHeaders(secret);
            for (var attempt = 0; attempt < MaximumTurnAttempts; attempt++)
            {
                var receivedSseEvent = false;
                var decoder = protocol.CreateDecoder(SanitizeFailureEvents(onEvent, secret));
                try
                {
                    await _transport.SendSseAsync(uri, json, headers, value =>
                    {
                        receivedSseEvent = true;
                        decoder.Accept(value);
                    }, cancellationToken).ConfigureAwait(false);
                    return decoder.Complete();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (attempt + 1 < MaximumTurnAttempts &&
                                                  !receivedSseEvent && IsTransientTurnFailure(exception))
                {
                    await Task.Delay(RetryDelay(exception, attempt), cancellationToken).ConfigureAwait(false);
                }
            }
            throw new InvalidOperationException("HTTP Agent retry loop ended without a result.");
        }

        public async Task<IReadOnlyList<string>> ListModelsAsync(
            AgentProviderProfile profile,
            CancellationToken cancellationToken)
        {
            var discovery = await DiscoverModelsAsync(profile, cancellationToken).ConfigureAwait(false);
            var result = new List<string>(discovery.Models.Count);
            foreach (var model in discovery.Models) result.Add(model.Id);
            return result;
        }

        public async Task<AgentModelDiscoveryResult> DiscoverModelsAsync(
            AgentProviderProfile profile,
            CancellationToken cancellationToken)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            var profileSnapshot = Snapshot(profile);
            var preset = AgentProviderCatalog.FindProvider(profileSnapshot);
            if (preset != null && !preset.SupportsRemoteModelList)
                return AgentProviderCatalog.CuratedResult(profileSnapshot, AgentModelDiscoverySource.CuratedOnly);

            var protocol = AgentWireProtocolFactory.Create(profileSnapshot.Protocol);
            try
            {
                var secret = profileSnapshot.ApiKey;
                if (preset != null && string.IsNullOrWhiteSpace(secret))
                    return AgentProviderCatalog.CuratedResult(profileSnapshot,
                        AgentModelDiscoverySource.CuratedFallback,
                        $"No API key is available for {preset.DisplayName}; showing the maintained fallback catalog.");
                var json = await _transport.GetJsonAsync(
                    ResolveUri(profileSnapshot.BaseUrl, protocol.ModelsPath),
                    protocol.CreateHeaders(secret),
                    cancellationToken).ConfigureAwait(false);
                var remoteModels = protocol.ParseModels(json);
                if (remoteModels.Count > 0)
                    return AgentProviderCatalog.MergeRemoteModels(profileSnapshot, remoteModels);
                if (preset == null || preset.Models.Count == 0)
                    return AgentProviderCatalog.MergeRemoteModels(profileSnapshot, remoteModels);
                return AgentProviderCatalog.CuratedResult(profileSnapshot,
                    AgentModelDiscoverySource.CuratedFallback,
                    "The provider Models API returned no models; showing the maintained fallback catalog.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (CanUseCuratedFallback(exception, preset))
            {
                return AgentProviderCatalog.CuratedResult(profileSnapshot,
                    AgentModelDiscoverySource.CuratedFallback,
                    "Remote model discovery failed; showing the maintained fallback catalog. " + exception.Message);
            }
        }

        public void Dispose()
        {
            _transport.Dispose();
        }

        internal static Uri ResolveUri(string baseUrl, string relativePath)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
                throw new AgentProviderException("Provider Base URL is not an absolute HTTP(S) URI.");
            if (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
                throw new AgentProviderException("Provider Base URL must use HTTP or HTTPS.");
            if (!string.IsNullOrEmpty(baseUri.UserInfo))
                throw new AgentProviderException("Provider Base URL must not contain embedded credentials.");
            if (!string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
                throw new AgentProviderException("Provider Base URL must not contain a query or fragment.");
            var normalized = baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
                ? baseUri
                : new Uri(baseUri.AbsoluteUri + "/", UriKind.Absolute);
            return new Uri(normalized, relativePath);
        }

        private static AgentProviderProfile Snapshot(AgentProviderProfile profile)
        {
            return new AgentProviderProfile
            {
                Id = profile.Id,
                ProviderPresetId = profile.ProviderPresetId,
                Name = profile.Name,
                Protocol = profile.Protocol,
                BaseUrl = profile.BaseUrl,
                Model = profile.Model,
                ReasoningEffort = profile.ReasoningEffort,
                ApiKey = profile.ApiKey,
                MaxOutputTokens = profile.MaxOutputTokens,
                ContextWindowTokens = profile.ContextWindowTokens,
                StrictTools = profile.StrictTools
            };
        }

        private static bool CanUseCuratedFallback(Exception exception, AgentProviderPreset? preset)
        {
            if (preset == null || preset.Models.Count == 0) return false;
            return exception is AgentProviderException or HttpRequestException or FormatException or
                   LitJson.JsonException;
        }

        private static bool IsTransientTurnFailure(Exception exception) =>
            exception is HttpRequestException ||
            exception is TaskCanceledException ||
            exception is AgentProviderTransportException { IsTransient: true };

        private static TimeSpan RetryDelay(Exception exception, int completedAttempt)
        {
            var fallback = completedAttempt == 0 ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(1.5);
            if (exception is not AgentProviderTransportException { RetryAfter: { } retryAfter }) return fallback;
            return retryAfter > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : retryAfter;
        }

        private static Action<AgentStreamEvent>? SanitizeFailureEvents(
            Action<AgentStreamEvent>? onEvent,
            string secret)
        {
            if (onEvent == null || string.IsNullOrEmpty(secret)) return onEvent;
            return value =>
            {
                if (value.Kind != AgentStreamEventKind.RunFailed ||
                    value.Text.IndexOf(secret, StringComparison.Ordinal) < 0)
                {
                    onEvent(value);
                    return;
                }

                onEvent(new AgentStreamEvent(value.Kind,
                    value.Text.Replace(secret, "<redacted>"), value.CallId));
            };
        }
    }
}
