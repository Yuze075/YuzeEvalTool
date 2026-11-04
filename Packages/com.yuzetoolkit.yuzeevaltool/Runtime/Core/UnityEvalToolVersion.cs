#nullable enable
using System;

namespace YuzeToolkit.Eval
{
    public static class UnityEvalToolVersion
    {
        public const string Current = "4.2.0";
        public const string RequiredBrokerVersion = "4.2.0";
        public const string InstallBrokerMenuPath = "YuzeToolkit/Eval Tool/Install Latest npm Package";

        public static string GetBrokerVersionWarning(string? reportedVersion)
        {
            var core = reportedVersion?.Split('-', '+')[0];
            if (Version.TryParse(core, out var actual) && actual.Build >= 0)
            {
                var comparison = new Version(actual.Major, actual.Minor, actual.Build)
                    .CompareTo(new Version(RequiredBrokerVersion));
                if (comparison > 0 || comparison == 0 && reportedVersion!.IndexOf('-') < 0)
                    return string.Empty;
            }
            var reported = string.IsNullOrWhiteSpace(reportedVersion) ? "not reported by this Broker" : reportedVersion;
            return $"Broker version {reported}; this Unity package requires {RequiredBrokerVersion} or newer. " +
                   $"Update from '{InstallBrokerMenuPath}', or run 'npm install --global @yuzetoolkit/unityevaltool@latest'.";
        }
    }
}
