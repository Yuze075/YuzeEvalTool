#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YuzeToolkit.UnityAgent
{
    public class AgentProviderException : Exception
    {
        public AgentProviderException(string message) : base(message)
        {
        }

        public AgentProviderException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    internal sealed class AgentProviderTransportException : AgentProviderException
    {
        public AgentProviderTransportException(string message, HttpStatusCode statusCode,
            TimeSpan? retryAfter) : base(message)
        {
            StatusCode = statusCode;
            RetryAfter = retryAfter;
        }

        public HttpStatusCode StatusCode { get; }
        public TimeSpan? RetryAfter { get; }
        public bool IsTransient => StatusCode == HttpStatusCode.RequestTimeout ||
                                   (int)StatusCode == 425 || StatusCode == (HttpStatusCode)429 ||
                                   (int)StatusCode >= 500;
    }

    internal sealed class AgentProviderTransport : IDisposable
    {
        private const int MaxErrorBodyBytes = 16 * 1024;
        private const int MaxJsonBodyBytes = 8 * 1024 * 1024;
        private readonly HttpClient _client;
        private readonly bool _ownsClient;

        public AgentProviderTransport(HttpClient? client = null)
        {
            _client = client ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            _ownsClient = client == null;
        }

        public async Task SendSseAsync(
            Uri uri,
            string json,
            IReadOnlyDictionary<string, string> headers,
            Action<SseEvent> onEvent,
            CancellationToken cancellationToken)
        {
            using var request = CreateRequest(HttpMethod.Post, uri, headers);
            request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw await CreateHttpExceptionAsync(response, headers, cancellationToken).ConfigureAwait(false);
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrWhiteSpace(mediaType) &&
                !string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var unexpectedBody = await ReadBodyAsync(response.Content, MaxErrorBodyBytes,
                    cancellationToken).ConfigureAwait(false);
                throw new AgentProviderException(
                    $"Provider returned '{mediaType}' instead of an SSE response.\n" +
                    RedactSecrets(FormatBody(unexpectedBody), headers));
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            try
            {
                await SseParser.ParseAsync(stream, onEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (AgentProviderException exception)
            {
                var message = RedactSecrets(exception.Message, headers);
                if (string.Equals(message, exception.Message, StringComparison.Ordinal)) throw;
                throw new AgentProviderException(message, exception);
            }
            catch (Exception exception) when (exception is InvalidDataException or DecoderFallbackException or
                                               FormatException or OverflowException or LitJson.JsonException)
            {
                throw new AgentProviderException("Provider returned an invalid SSE stream.", exception);
            }
        }

        public async Task<string> GetJsonAsync(
            Uri uri,
            IReadOnlyDictionary<string, string> headers,
            CancellationToken cancellationToken)
        {
            using var request = CreateRequest(HttpMethod.Get, uri, headers);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw await CreateHttpExceptionAsync(response, headers, cancellationToken).ConfigureAwait(false);
            var body = await ReadBodyAsync(response.Content, MaxJsonBodyBytes, cancellationToken)
                .ConfigureAwait(false);
            if (body.Truncated)
                throw new AgentProviderException($"Provider JSON response exceeded the {MaxJsonBodyBytes}-byte limit.");
            return body.Text;
        }

        public void Dispose()
        {
            if (_ownsClient) _client.Dispose();
        }

        private static HttpRequestMessage CreateRequest(
            HttpMethod method,
            Uri uri,
            IReadOnlyDictionary<string, string> headers)
        {
            var request = new HttpRequestMessage(method, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", "UnityAgentTool/0.1.0");
            foreach (var pair in headers)
            {
                if (pair.Key.IndexOf('\r') >= 0 || pair.Key.IndexOf('\n') >= 0 ||
                    pair.Value.IndexOf('\r') >= 0 || pair.Value.IndexOf('\n') >= 0)
                {
                    request.Dispose();
                    throw new AgentProviderException($"Provider header '{pair.Key}' contains a line break.");
                }

                if (!request.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
                {
                    request.Dispose();
                    throw new AgentProviderException($"Provider header '{pair.Key}' could not be applied.");
                }
            }
            return request;
        }

        private static async Task<AgentProviderException> CreateHttpExceptionAsync(
            HttpResponseMessage response,
            IReadOnlyDictionary<string, string> requestHeaders,
            CancellationToken cancellationToken)
        {
            var body = await ReadBodyAsync(response.Content, MaxErrorBodyBytes, cancellationToken)
                .ConfigureAwait(false);
            TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
            if (retryAfter == null && response.Headers.RetryAfter?.Date is { } date)
                retryAfter = date - DateTimeOffset.UtcNow;
            if (retryAfter is { } delay && delay < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
            return new AgentProviderTransportException(
                $"Provider returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.\n" +
                RedactSecrets(FormatBody(body), requestHeaders), response.StatusCode, retryAfter);
        }

        private static async Task<BoundedBody> ReadBodyAsync(
            HttpContent content,
            int maxBytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
            using var output = new MemoryStream(Math.Min(maxBytes, 32 * 1024));
            var buffer = new byte[8192];
            while (output.Length < maxBytes)
            {
                var remaining = maxBytes - (int)output.Length;
                var read = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, remaining), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    return new BoundedBody(Encoding.UTF8.GetString(output.ToArray()), false);
                output.Write(buffer, 0, read);
            }

            var truncated = await stream.ReadAsync(buffer, 0, 1, cancellationToken).ConfigureAwait(false) > 0;
            return new BoundedBody(Encoding.UTF8.GetString(output.ToArray()), truncated);
        }

        private static string FormatBody(BoundedBody body)
        {
            if (string.IsNullOrWhiteSpace(body.Text))
                return "(empty response body)";
            return body.Truncated ? body.Text + "\n…(truncated)" : body.Text;
        }

        private static string RedactSecrets(
            string value,
            IReadOnlyDictionary<string, string> requestHeaders)
        {
            foreach (var pair in requestHeaders)
            {
                if (!IsSensitiveHeader(pair.Key) || string.IsNullOrEmpty(pair.Value)) continue;
                value = value.Replace(pair.Value, "<redacted>");
                const string bearerPrefix = "Bearer ";
                if (pair.Value.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase) &&
                    pair.Value.Length > bearerPrefix.Length)
                    value = value.Replace(pair.Value.Substring(bearerPrefix.Length), "<redacted>");
            }
            return value;
        }

        private static bool IsSensitiveHeader(string name) =>
            string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "x-api-key", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "api-key", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "x-goog-api-key", StringComparison.OrdinalIgnoreCase);

        private readonly struct BoundedBody
        {
            public BoundedBody(string text, bool truncated)
            {
                Text = text;
                Truncated = truncated;
            }

            public string Text { get; }

            public bool Truncated { get; }
        }
    }
}
