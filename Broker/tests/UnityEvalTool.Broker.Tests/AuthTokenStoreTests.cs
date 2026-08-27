using System.Text.Json;
using Xunit;

namespace YuzeToolkit.Eval.Broker.Tests;

public sealed class AuthTokenStoreTests
{
    [Fact]
    public async Task ConcurrentStoresPublishOneAtomicToken()
    {
        var root = Path.Combine(Path.GetTempPath(), "unityevaltool-auth-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "auth.json");
        Directory.CreateDirectory(root);
        try
        {
            using var start = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            {
                var store = new AuthTokenStore(path);
                start.Wait();
                return store.GetOrCreateToken();
            })).ToArray();
            start.Set();
            var tokens = await Task.WhenAll(tasks);

            Assert.Single(tokens.Distinct(StringComparer.Ordinal));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(tokens[0], document.RootElement.GetProperty("token").GetString());
            Assert.Empty(Directory.GetFiles(root, ".auth.*.tmp"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExistingMalformedTokenFailsExplicitly()
    {
        var root = Path.Combine(Path.GetTempPath(), "unityevaltool-auth-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "auth.json");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(path, "not-json");
            Assert.Throws<InvalidDataException>(() => new AuthTokenStore(path).GetOrCreateToken());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadingOptionalMissingTokenDoesNotCreateAuthFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "unityevaltool-auth-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "auth.json");
        try
        {
            Assert.Null(new AuthTokenStore(path).TryReadExistingToken());
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
