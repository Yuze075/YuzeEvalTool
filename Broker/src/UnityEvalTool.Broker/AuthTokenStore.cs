using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YuzeToolkit.UnityEvalTool.Broker;

internal sealed class AuthTokenStore
{
    public const int DefaultMaxStoredTokens = 5;
    public const int HardMaxStoredTokens = 32;
    public const int MaxTokenLength = 256;
    private const int PublicationAttempts = 100;
    private const int PublicationDelayMilliseconds = 20;

    private readonly string _filePath;
    private readonly string _configPath;
    private readonly object _syncRoot = new();

    public AuthTokenStore()
    {
        var userRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userRoot))
            throw new InvalidOperationException("The current user profile directory is unavailable.");
        var directory = Path.Combine(userRoot, ".unityevaltool");
        _filePath = Path.Combine(directory, "auth.json");
        _configPath = Path.Combine(directory, "config.json");
    }

    internal AuthTokenStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Auth token path is required.", nameof(filePath));
        _filePath = Path.GetFullPath(filePath);
        _configPath = Path.Combine(Path.GetDirectoryName(_filePath)!, "config.json");
    }

    public IReadOnlyList<string> GetTokens()
    {
        lock (_syncRoot)
        {
            var tokens = ReadTokensIfPresent(_filePath);
            ValidateCapacity(tokens, ReadMaxStoredTokens());
            return tokens;
        }
    }

    public bool AddTokenList(string? tokenList)
    {
        var additions = ParseTokenList(tokenList);
        if (additions.Count == 0) return false;

        lock (_syncRoot)
        {
            EnsureDirectory();
            using var publicationMutex = CreatePublicationMutex(_filePath);
            var ownsPublicationMutex = false;
            try
            {
                try
                {
                    publicationMutex.WaitOne();
                }
                catch (AbandonedMutexException)
                {
                    // The previous writer terminated; ownership is still granted to this process.
                }
                ownsPublicationMutex = true;

                var existing = ReadTokensIfPresent(_filePath);
                var merged = new List<string>(existing);
                foreach (var token in additions)
                {
                    if (!merged.Contains(token, StringComparer.Ordinal)) merged.Add(token);
                }

                ValidateCapacity(merged, ReadMaxStoredTokens());
                if (merged.Count == existing.Count) return false;
                WriteTokensAtomically(merged);
                return true;
            }
            finally
            {
                if (ownsPublicationMutex) publicationMutex.ReleaseMutex();
            }
        }
    }

    // Retained for source compatibility with the previous optional global Broker-token mode.
    // New product code never creates a token implicitly; it only stores explicit MCP/CLI/user input.
    public string GetOrCreateToken()
    {
        lock (_syncRoot)
        {
            EnsureDirectory();
            using var publicationMutex = CreatePublicationMutex(_filePath);
            var ownsPublicationMutex = false;
            try
            {
                try
                {
                    publicationMutex.WaitOne();
                }
                catch (AbandonedMutexException)
                {
                    // The previous writer terminated; ownership is still granted to this process.
                }
                ownsPublicationMutex = true;
                var existing = ReadTokensIfPresent(_filePath);
                if (existing.Count > 0) return existing[0];
                var generated = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                WriteTokensAtomically(new[] { generated });
                return generated;
            }
            finally
            {
                if (ownsPublicationMutex) publicationMutex.ReleaseMutex();
            }
        }
    }

    public string? TryReadExistingToken()
    {
        var tokens = GetTokens();
        return tokens.Count == 0 ? null : tokens[0];
    }

    public bool IsValid(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var supplied = Encoding.UTF8.GetBytes(token);
        foreach (var expected in GetTokens())
        {
            var candidate = Encoding.UTF8.GetBytes(expected);
            if (candidate.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(candidate, supplied))
                return true;
        }
        return false;
    }

    public string FilePath => _filePath;
    public string ConfigPath => _configPath;
    public int MaxStoredTokens => ReadMaxStoredTokens();

    private static IReadOnlyList<string> ParseTokenList(string? tokenList)
    {
        if (string.IsNullOrEmpty(tokenList)) return Array.Empty<string>();
        var values = tokenList.Split('/');
        var result = new List<string>(values.Length);
        foreach (var value in values)
        {
            ValidateToken(value);
            if (!result.Contains(value, StringComparer.Ordinal)) result.Add(value);
        }
        return result;
    }

    private static void ValidateToken(string token)
    {
        if (token.Length == 0)
            throw new InvalidDataException("Yuze Eval Tool token lists cannot contain empty entries.");
        if (token.Length > MaxTokenLength)
            throw new InvalidDataException($"Yuze Eval Tool tokens cannot exceed {MaxTokenLength} characters.");
        foreach (var character in token)
        {
            var allowed = character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-';
            if (!allowed)
                throw new InvalidDataException(
                    "Yuze Eval Tool tokens may contain only ASCII letters, digits, underscore, and hyphen; slash separates tokens.");
        }
    }

    private IReadOnlyList<string> ReadTokensIfPresent(string path)
    {
        if (!File.Exists(path)) return Array.Empty<string>();
        TryRestrictPermissions(path);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var tokens = new List<string>();
            if (root.TryGetProperty("tokens", out var tokenArray))
            {
                if (tokenArray.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException($"Yuze Eval Tool auth token file '{path}' has a non-array tokens value.");
                foreach (var element in tokenArray.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String)
                        throw new InvalidDataException($"Yuze Eval Tool auth token file '{path}' contains a non-string token.");
                    var token = element.GetString()!;
                    ValidateToken(token);
                    if (!tokens.Contains(token, StringComparer.Ordinal)) tokens.Add(token);
                }
            }
            else if (root.TryGetProperty("token", out var legacyToken) && legacyToken.ValueKind == JsonValueKind.String)
            {
                var token = legacyToken.GetString()!;
                ValidateToken(token);
                tokens.Add(token);
            }
            else
            {
                throw new InvalidDataException(
                    $"Yuze Eval Tool auth token file '{path}' does not contain a tokens array or legacy token.");
            }
            return tokens;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Yuze Eval Tool auth token file '{path}' is invalid JSON.", ex);
        }
    }

    private int ReadMaxStoredTokens()
    {
        if (!File.Exists(_configPath)) return DefaultMaxStoredTokens;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_configPath));
            if (!document.RootElement.TryGetProperty("maxStoredTokens", out var value))
                return DefaultMaxStoredTokens;
            if (!value.TryGetInt32(out var maximum) || maximum is < 1 or > HardMaxStoredTokens)
                throw new InvalidDataException(
                    $"Yuze Eval Tool config maxStoredTokens must be between 1 and {HardMaxStoredTokens}.");
            return maximum;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Yuze Eval Tool config file '{_configPath}' is invalid JSON.", ex);
        }
    }

    private static void ValidateCapacity(IReadOnlyCollection<string> tokens, int maximum)
    {
        if (tokens.Count > maximum)
            throw new InvalidDataException(
                $"Yuze Eval Tool stores at most {maximum} tokens; the requested set contains {tokens.Count}.");
    }

    private void WriteTokensAtomically(IReadOnlyList<string> tokens)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        var temporaryPath = Path.Combine(directory, ".auth." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 2);
                // Keep the first token for readers from the previous release while all new code uses tokens[].
                writer.WriteString("token", tokens[0]);
                writer.WriteStartArray("tokens");
                foreach (var token in tokens) writer.WriteStringValue(token);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            File.AppendAllText(temporaryPath, Environment.NewLine);
            TryRestrictPermissions(temporaryPath);
            PublishTemporaryFile(temporaryPath);
            TryRestrictPermissions(_filePath);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
        }
    }

    private void PublishTemporaryFile(string temporaryPath)
    {
        IOException? lastError = null;
        for (var attempt = 0; attempt < PublicationAttempts; attempt++)
        {
            try
            {
                File.Move(temporaryPath, _filePath, true);
                return;
            }
            catch (IOException ex)
            {
                lastError = ex;
                Thread.Sleep(PublicationDelayMilliseconds);
            }
        }

        throw new IOException($"Timed out publishing Yuze Eval Tool auth tokens '{_filePath}'.", lastError);
    }

    private void EnsureDirectory()
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        TryRestrictDirectoryPermissions(directory);
    }

    private static Mutex CreatePublicationMutex(string path)
    {
        var identity = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return new Mutex(false, "UnityEvalTool.AuthToken." + hash);
    }

    private static void TryRestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void TryRestrictDirectoryPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
