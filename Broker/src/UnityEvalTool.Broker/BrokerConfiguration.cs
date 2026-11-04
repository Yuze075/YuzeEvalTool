#nullable enable
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YuzeToolkit.Eval.Broker;

internal sealed partial class BrokerConfiguration
{
    public List<RemoteBrokerOptions> Remotes { get; set; } = [];
    public RemoteAccessOptions RemoteAccess { get; set; } = new();
    public WorkspaceOptions Workspace { get; set; } = new();
    public int DiscoveryIntervalSeconds { get; set; } = 10;
    public int ConnectionTimeoutSeconds { get; set; } = 10;
    public int ReconnectMaxDelaySeconds { get; set; } = 60;
    public int RemoteRetentionSeconds { get; set; } = 300;
    public int MaxConcurrentDiscoveries { get; set; } = 4;
    public int TransportGraceSeconds { get; set; } = 10;

    [JsonIgnore] public string BrokerId { get; private set; } = string.Empty;
    [JsonIgnore] public string FilePath { get; private set; } = string.Empty;
    [JsonIgnore] public IReadOnlyDictionary<string, string> Credentials { get; private set; } = new Dictionary<string, string>();

    public static string DefaultPath => Path.Combine(InstallMetadataStore.ConfigDirectory, "config.json");

    public static BrokerConfiguration Load(string path, bool createIdentity = true)
    {
        path = Path.GetFullPath(path);
        var config = File.Exists(path)
            ? JsonSerializer.Deserialize(File.ReadAllText(path), BrokerJsonContext.Default.BrokerConfiguration)
              ?? throw new InvalidDataException("Broker configuration must be a JSON object.")
            : new BrokerConfiguration();
        config.FilePath = path;
        var directory = Path.GetDirectoryName(path)!;
        var credentialPath = Path.Combine(directory, "peer-auth.json");
        if (File.Exists(credentialPath))
        {
            RestrictFile(credentialPath);
            config.Credentials = JsonSerializer.Deserialize(File.ReadAllText(credentialPath),
                BrokerJsonContext.Default.DictionaryStringString)
                ?? throw new InvalidDataException("peer-auth.json must contain a credential-name to secret object.");
            foreach (var pair in config.Credentials)
            {
                ValidateId(pair.Key);
                ValidateSecret(pair.Key, pair.Value);
            }
        }
        config.Validate();
        config._projects = config.Workspace.Projects.ToArray();
        if (createIdentity)
        {
            Directory.CreateDirectory(directory);
            var identityPath = Path.Combine(directory, "broker-id");
            if (!File.Exists(identityPath))
            {
                // Publish complete contents atomically; concurrent starts never observe a partial identity.
                var temporaryPath = identityPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporaryPath, Guid.NewGuid().ToString("N"));
                    RestrictFile(temporaryPath);
                    try { File.Move(temporaryPath, identityPath); }
                    catch (IOException) when (File.Exists(identityPath)) { }
                }
                finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            }
            config.BrokerId = File.ReadAllText(identityPath).Trim();
            if (!Guid.TryParseExact(config.BrokerId, "N", out _))
                throw new InvalidDataException("broker-id must contain a GUID in N format.");
        }
        return config;
    }

    public string? GetCredential(string? reference) => reference == null ? null : Credentials[reference];

    public bool AcceptsPeerToken(string authorization)
    {
        if (RemoteAccess.AuthMode == "none") return true;
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var supplied = Encoding.UTF8.GetBytes(authorization[7..]);
        var expected = Encoding.UTF8.GetBytes(GetCredential(RemoteAccess.CredentialRef)!);
        return CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    private void Validate()
    {
        if (Remotes == null || RemoteAccess == null || Workspace == null)
            throw new InvalidDataException("remotes and remoteAccess cannot be null.");
        RequireRange(DiscoveryIntervalSeconds, 1, 3600, "discoveryIntervalSeconds");
        RequireRange(ConnectionTimeoutSeconds, 1, 300, "connectionTimeoutSeconds");
        RequireRange(ReconnectMaxDelaySeconds, 1, 3600, "reconnectMaxDelaySeconds");
        RequireRange(RemoteRetentionSeconds, 0, 86400, "remoteRetentionSeconds");
        RequireRange(MaxConcurrentDiscoveries, 1, 32, "maxConcurrentDiscoveries");
        RequireRange(TransportGraceSeconds, 1, 300, "transportGraceSeconds");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var remote in Remotes)
        {
            if (remote == null) throw new InvalidDataException("remotes cannot contain null.");
            ValidateId(remote.Id);
            if (!ids.Add(remote.Id)) throw new InvalidDataException($"Duplicate remote id '{remote.Id}'.");
            RequireRange(remote.Port, 1, 65535, "remote port");
            if (remote.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(remote.Host) ||
                Uri.CheckHostName(remote.Host.Trim('[', ']')) == UriHostNameType.Unknown)
                throw new InvalidDataException($"Remote '{remote.Id}' needs a valid host and http/https scheme.");
            if (!endpoints.Add(remote.Endpoint.AbsoluteUri))
                throw new InvalidDataException("The same remote endpoint cannot be configured twice.");
            ValidateCredential(remote.CredentialRef);
        }
        if (RemoteAccess.AuthMode is not ("none" or "token"))
            throw new InvalidDataException("remoteAccess.authMode must be none or token.");
        RequireRange(RemoteAccess.Port, 1, 65535, "remoteAccess.port");
        if (!IPAddress.TryParse(RemoteAccess.ListenAddress, out _))
            throw new InvalidDataException("remoteAccess.listenAddress must be an IP address.");
        if (RemoteAccess.AllowedHosts == null || RemoteAccess.AllowedOrigins == null ||
            RemoteAccess.AllowedHosts.Any(host => string.IsNullOrWhiteSpace(host) || host.Contains('*') ||
                Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown) ||
            RemoteAccess.AllowedOrigins.Any(origin => !Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || uri.GetLeftPart(UriPartial.Authority) != origin))
            throw new InvalidDataException("Network hosts and origins must be explicit valid hosts / HTTP origins without wildcards.");
        Workspace.Validate();
        if (RemoteAccess.Enabled)
        {
            if (RemoteAccess.AuthMode == "token" && RemoteAccess.CredentialRef == null)
                throw new InvalidDataException("Token-protected remote access requires credentialRef.");
            ValidateCredential(RemoteAccess.CredentialRef);
            if (IPAddress.Parse(RemoteAccess.ListenAddress).Equals(IPAddress.Any) ||
                IPAddress.Parse(RemoteAccess.ListenAddress).Equals(IPAddress.IPv6Any))
            {
                if (RemoteAccess.AllowedHosts.Count == 0)
                    throw new InvalidDataException("Wildcard listening requires explicit remoteAccess.allowedHosts.");
            }
        }
    }

    internal void ValidateCredential(string? reference)
    {
        if (reference == null) return;
        ValidateId(reference);
        if (!Credentials.ContainsKey(reference))
            throw new InvalidDataException($"Credential '{reference}' is missing from peer-auth.json.");
    }

    internal static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 ||
            id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new InvalidDataException("Remote and credential ids must be 1–64 ASCII letters, digits, hyphens or underscores.");
    }

    internal static void ValidateSecret(string reference, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 256 || secret.Any(character => character < '!' || character > '~'))
            throw new InvalidDataException($"Credential '{reference}' must contain 1–256 printable ASCII characters without spaces.");
    }

    internal static void RequireRange(int value, int min, int max, string name)
    {
        if (value < min || value > max) throw new InvalidDataException($"{name} must be between {min} and {max}.");
    }

    internal static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

internal sealed class RemoteBrokerOptions
{
    public string Id { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 2348;
    public string Scheme { get; set; } = "http";
    public string? CredentialRef { get; set; }
    public bool Enabled { get; set; } = true;
    [JsonIgnore] public Uri Endpoint => new UriBuilder(Scheme, Host, Port, "/mcp").Uri;
}

internal sealed class RemoteAccessOptions
{
    public bool Enabled { get; set; }
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 2348;
    public string AuthMode { get; set; } = "token";
    public string? CredentialRef { get; set; }
    public List<string> AllowedHosts { get; set; } = [];
    public List<string> AllowedOrigins { get; set; } = [];
}
