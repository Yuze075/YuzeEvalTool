using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace YuzeToolkit.Eval.Broker;

internal static class UnityToolResultConverter
{
    public static CallToolResult Convert(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest,
                "Unity eval returned a non-object tool result.");

        if (!payload.TryGetProperty("content", out var contentElement) ||
            contentElement.ValueKind != JsonValueKind.Array)
            throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest,
                "Unity eval tool result requires a content array.");
        var content = new List<ContentBlock>();
        foreach (var block in contentElement.EnumerateArray())
            content.Add(ConvertContentBlock(block));

        var result = new CallToolResult { Content = content };
        if (payload.TryGetProperty("isError", out var isErrorElement))
        {
            if (isErrorElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest,
                    "Unity eval tool result isError must be a boolean.");
            result.IsError = isErrorElement.GetBoolean();
        }

        return result;
    }

    private static ContentBlock ConvertContentBlock(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object ||
            !block.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
            throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest,
                "Unity eval content blocks require a string type.");

        return typeElement.GetString() switch
        {
            "text" => new TextContentBlock
            {
                Text = RequireString(block, "text", "Unity eval text content requires text.")
            },
            "image" => ConvertImage(block),
            var type => throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest,
                $"Unity eval returned unsupported MCP content type '{type}'.")
        };
    }

    private static ImageContentBlock ConvertImage(JsonElement block)
    {
        var base64 = RequireString(block, "data", "Unity eval image content requires base64 data.");
        byte[] data;
        try { data = System.Convert.FromBase64String(base64); }
        catch (FormatException ex)
        {
            throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest,
                $"Unity eval image content contains invalid base64 data: {ex.Message}");
        }
        return ImageContentBlock.FromBytes(data,
            RequireString(block, "mimeType", "Unity eval image content requires mimeType."));
    }

    private static string RequireString(JsonElement element, string propertyName, string error)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
            throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest, error);
        return property.GetString()!;
    }
}
