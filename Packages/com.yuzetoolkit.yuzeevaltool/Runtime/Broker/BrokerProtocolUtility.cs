#nullable enable
using System;
using System.Collections.Generic;

namespace YuzeToolkit
{
    internal static class BrokerProtocolUtility
    {
        public const string ProtocolVersion = "2.0";
        public const string Endpoint = "ws://127.0.0.1:2347/unity";

        public static Dictionary<string, object?> ParseEnvelope(string json)
        {
            var envelope = EvalData.AsObject(EvalJson.Parse(json))
                           ?? throw new InvalidOperationException("Broker message must be a JSON object.");
            var protocol = EvalData.GetString(envelope, "protocol") ?? string.Empty;
            if (!string.Equals(protocol, ProtocolVersion, StringComparison.Ordinal))
                throw new InvalidOperationException($"Unsupported Broker protocol '{protocol}'.");
            return envelope;
        }

        public static string Request(string id, string method, Dictionary<string, object?> payload) =>
            EvalJson.Stringify(EvalData.Obj(
                ("protocol", ProtocolVersion),
                ("type", "request"),
                ("id", id),
                ("method", method),
                ("payload", payload),
                ("error", null)));

        public static string Response(string id, string method, object? payload, Dictionary<string, object?>? error = null) =>
            EvalJson.Stringify(EvalData.Obj(
                ("protocol", ProtocolVersion),
                ("type", "response"),
                ("id", id),
                ("method", method),
                ("payload", payload ?? EvalData.Obj()),
                ("error", error)));

        public static string Event(string method, object? payload) =>
            EvalJson.Stringify(EvalData.Obj(
                ("protocol", ProtocolVersion),
                ("type", "event"),
                ("id", null),
                ("method", method),
                ("payload", payload ?? EvalData.Obj()),
                ("error", null)));
    }
}
