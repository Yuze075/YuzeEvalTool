#nullable enable

namespace YuzeToolkit
{
    public sealed class EvalOptions
    {
        public int MaxRequestBodyBytes { get; set; } = 1024 * 1024;

        public int DefaultEvalTimeoutSeconds { get; set; } = 30;
    }
}
