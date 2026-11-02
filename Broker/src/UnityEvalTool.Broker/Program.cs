#nullable enable
namespace YuzeToolkit.Eval.Broker;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var isolatedConfiguration = args.Length > 0 &&
                (string.Equals(args[0], "broker", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(args[0], "remote", StringComparison.OrdinalIgnoreCase)) &&
                args.Any(argument => argument == "--config" || argument.StartsWith("--config=", StringComparison.Ordinal));
            if (!isolatedConfiguration)
                InstallMetadataStore.RegisterCurrentExecutable();
            if (args.Length >= 1 && string.Equals(args[0], "broker", StringComparison.OrdinalIgnoreCase))
            {
                await BrokerHost.RunAsync(args.Skip(1).ToArray());
                return 0;
            }

            return await CliApplication.RunAsync(args, CancellationToken.None);
        }
        catch (IOException ex) when (ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"BrokerUnavailable: a configured listener address is already in use. {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
