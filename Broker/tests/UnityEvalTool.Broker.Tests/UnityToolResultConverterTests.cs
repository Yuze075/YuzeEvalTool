using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace YuzeToolkit.Eval.Broker.Tests;

public sealed class UnityToolResultConverterTests
{
    [Fact]
    public void ConvertsTextImageAndErrorToNativeMcpWireShape()
    {
        using var source = JsonDocument.Parse("""
        {
          "content": [
            { "type": "text", "text": "failed visibly" },
            { "type": "image", "data": "AQID", "mimeType": "image/png" }
          ],
          "isError": true
        }
        """);

        var result = UnityToolResultConverter.Convert(source.RootElement);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]);
        Assert.Equal("failed visibly", text.Text);
        var image = Assert.IsType<ImageContentBlock>(result.Content[1]);
        Assert.Equal(new byte[] { 1, 2, 3 }, image.DecodedData.ToArray());
        Assert.Equal("AQID", System.Text.Encoding.UTF8.GetString(image.Data.Span));
        Assert.Equal("image/png", image.MimeType);

        var wire = JsonSerializer.SerializeToElement(result, McpJsonUtilities.DefaultOptions);
        Assert.True(wire.GetProperty("isError").GetBoolean());
        Assert.Equal("failed visibly", wire.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("AQID", wire.GetProperty("content")[1].GetProperty("data").GetString());
        Assert.Equal("image/png", wire.GetProperty("content")[1].GetProperty("mimeType").GetString());
    }

    [Fact]
    public void RejectsUnknownContentInsteadOfNestingOpaqueJson()
    {
        using var source = JsonDocument.Parse("""{"content":[{"type":"unknown"}]}""");

        var error = Assert.Throws<BrokerOperationException>(() =>
            UnityToolResultConverter.Convert(source.RootElement));

        Assert.Equal(BrokerErrorCodes.InvalidRequest, error.Code);
    }

    [Fact]
    public void RejectsInvalidImageBase64()
    {
        using var source = JsonDocument.Parse(
            """{"content":[{"type":"image","data":"not base64!","mimeType":"image/png"}]}""");

        var error = Assert.Throws<BrokerOperationException>(() =>
            UnityToolResultConverter.Convert(source.RootElement));

        Assert.Equal(BrokerErrorCodes.InvalidRequest, error.Code);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"content\":null}")]
    public void RejectsMissingOrNonArrayContent(string json)
    {
        using var source = JsonDocument.Parse(json);
        var error = Assert.Throws<BrokerOperationException>(() =>
            UnityToolResultConverter.Convert(source.RootElement));
        Assert.Equal(BrokerErrorCodes.InvalidRequest, error.Code);
    }

    [Fact]
    public void AllowsEmptyContentArray()
    {
        using var source = JsonDocument.Parse("""{"content":[]}""");
        Assert.Empty(UnityToolResultConverter.Convert(source.RootElement).Content);
    }
}
