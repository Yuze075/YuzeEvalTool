#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YuzeToolkit.Eval.Broker;

internal static partial class CliApplication
{
    private static int ConfigureRemotes(string[] args)
    {
        var path = BrokerConfiguration.DefaultPath;
        var configIndex = Array.IndexOf(args, "--config");
        if (configIndex >= 0)
        {
            if (configIndex + 1 >= args.Length) throw new InvalidOperationException("--config requires a file path.");
            path = Path.GetFullPath(args[configIndex + 1]);
            args = args.Take(configIndex).Concat(args.Skip(configIndex + 2)).ToArray();
        }
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Console.WriteLine("""
Remote MCP configuration (changes take effect after `unity service restart`):
  unity remote list
  unity remote add <id> <host> <port> [credential-ref] [http|https]
  unity remote remove <id>
  unity remote credential <credential-ref>   Read one secret from stdin; never pass it as an argument.
  unity remote access off
  unity remote access none <listen-ip> <port>
  unity remote access token <listen-ip> <port> <credential-ref>

The network listener exports directly registered local Unity instances only.
No credential-ref means anonymous outbound access. The peer must allow it.
The existing Unity project token store is separate from these service credentials.
For additional host/origin allowances and timing settings, edit config.json.
Use --config <file> with any remote command to manage an isolated configuration.
""");
            return 0;
        }
        if (args[0] == "list")
        {
            if (args.Length != 1) throw new InvalidOperationException("Usage: unity remote list");
            var config = BrokerConfiguration.Load(path, createIdentity: false);
            Console.WriteLine("Config: " + path);
            Console.WriteLine("Credentials: " + Path.Combine(Path.GetDirectoryName(path)!, "peer-auth.json"));
            Console.WriteLine(config.RemoteAccess.Enabled
                ? $"Network export: {config.RemoteAccess.ListenAddress}:{config.RemoteAccess.Port} ({config.RemoteAccess.AuthMode})"
                : "Network export: disabled");
            foreach (var peer in config.Remotes)
                Console.WriteLine($"{peer.Id}  {peer.Endpoint}  {(peer.Enabled ? "enabled" : "disabled")}  " +
                                  (peer.CredentialRef == null ? "no service credential" : "service credential configured"));
            return 0;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var configLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        BrokerConfiguration.RestrictFile(path + ".lock");
        if (args[0] == "credential")
        {
            if (args.Length != 2) throw new InvalidOperationException("Usage: unity remote credential <credential-ref> (secret from stdin)");
            BrokerConfiguration.ValidateId(args[1]);
            if (!Console.IsInputRedirected)
                throw new InvalidOperationException("Provide the credential through redirected stdin to avoid terminal echo.");
            var secret = Console.ReadLine() ?? throw new InvalidDataException("No credential was supplied on stdin.");
            BrokerConfiguration.ValidateSecret(args[1], secret);
            var credentialPath = Path.Combine(Path.GetDirectoryName(path)!, "peer-auth.json");
            var credentials = BrokerConfiguration.ReadDocument(credentialPath);
            credentials[args[1]] = secret;
            foreach (var pair in credentials)
            {
                BrokerConfiguration.ValidateId(pair.Key);
                BrokerConfiguration.ValidateSecret(pair.Key, pair.Value?.GetValue<string>() ?? string.Empty);
            }
            BrokerConfiguration.WriteDocument(credentialPath, credentials, validateConfig: false);
        }
        else
        {
            var root = BrokerConfiguration.ReadDocument(path);
            switch (args[0])
            {
                case "add":
                    if (args.Length is < 4 or > 6)
                        throw new InvalidOperationException("Usage: unity remote add <id> <host> <port> [credential-ref] [http|https]");
                    BrokerConfiguration.ValidateId(args[1]);
                    if (!int.TryParse(args[3], out var port)) throw new InvalidDataException("Remote port must be an integer.");
                    var peers = root["remotes"] as JsonArray ?? new JsonArray();
                    if (root["remotes"] == null) root["remotes"] = peers;
                    if (peers.Any(peer => peer?["id"]?.GetValue<string>() == args[1]))
                        throw new InvalidDataException("Remote id already exists. Edit config.json or remove it first.");
                    peers.Add((JsonNode)new JsonObject
                    {
                        ["id"] = args[1], ["host"] = args[2], ["port"] = port,
                        ["credentialRef"] = args.Length > 4 && args[4] != "-" ? args[4] : null,
                        ["scheme"] = args.Length > 5 ? args[5] : "http", ["enabled"] = true
                    });
                    break;
                case "remove":
                    if (args.Length != 2) throw new InvalidOperationException("Usage: unity remote remove <id>");
                    var existing = root["remotes"] as JsonArray;
                    var selected = existing?.FirstOrDefault(peer => peer?["id"]?.GetValue<string>() == args[1]);
                    if (selected == null) throw new InvalidDataException("Remote id was not found.");
                    existing!.Remove(selected);
                    break;
                case "access":
                    if (args.Length == 2 && args[1] == "off")
                    {
                        root["remoteAccess"] = new JsonObject { ["enabled"] = false };
                        break;
                    }
                    if (args.Length is < 4 or > 5 || args[1] is not ("none" or "token") ||
                        (args[1] == "token") != (args.Length == 5) || !int.TryParse(args[3], out var accessPort))
                        throw new InvalidOperationException("Usage: unity remote access none|token <listen-ip> <port> [credential-ref]");
                    root["remoteAccess"] = new JsonObject
                    {
                        ["enabled"] = true, ["authMode"] = args[1], ["listenAddress"] = args[2],
                        ["port"] = accessPort, ["credentialRef"] = args.Length == 5 ? args[4] : null
                    };
                    break;
                default: throw new InvalidOperationException("Unknown remote command. Run `unity remote help`.");
            }
            BrokerConfiguration.WriteDocument(path, root, validateConfig: true);
        }
        Console.WriteLine("Saved. Run `unity service restart` to apply the configuration.");
        return 0;
    }

}
