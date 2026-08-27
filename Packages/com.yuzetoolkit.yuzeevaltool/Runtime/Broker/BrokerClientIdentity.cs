#nullable enable
using System;

namespace YuzeToolkit.Eval
{
    public sealed class BrokerClientIdentity
    {
        public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");
        public long ConnectionEpoch { get; set; } = 1;
        public long VmGeneration { get; set; } = 1;
    }
}
