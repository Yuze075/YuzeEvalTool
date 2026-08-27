namespace YuzeToolkit.Eval.Broker;

internal static class BrokerStatePolicy
{
    public static void ValidateWaitFor(string waitFor)
    {
        if (string.Equals(waitFor, "snapshot", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(waitFor, "ready", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(waitFor, "compilation-complete", StringComparison.OrdinalIgnoreCase))
            return;
        throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest,
            "waitFor must be snapshot, ready, or compilation-complete.");
    }

    public static bool IsRepairMode(UnityStatus status) =>
        string.Equals(status.Phase, "CompilationFailed", StringComparison.Ordinal);

    public static bool CanExecute(UnityStatus status) => status.CanEval || IsRepairMode(status);

    public static bool IsAuthorized(UnityInstanceSnapshot snapshot) =>
        !snapshot.AuthorizationRequired ||
        string.Equals(snapshot.AuthorizationState, "Authorized", StringComparison.Ordinal) ||
        string.Equals(snapshot.AuthorizationState, "NotRequired", StringComparison.Ordinal);

    public static void EnsureCanExecute(UnityInstanceSnapshot snapshot)
    {
        if (!IsAuthorized(snapshot))
            throw new BrokerOperationException(BrokerErrorCodes.UnityAuthorizationPending,
                "Unity is connected but its project token has not been verified.");
        if (CanExecute(snapshot.Status)) return;
        throw new BrokerOperationException(BrokerErrorCodes.UnityBusy,
            string.IsNullOrWhiteSpace(snapshot.Status.BusyReason)
                ? $"Unity is not ready for eval ({snapshot.Status.Phase})."
                : snapshot.Status.BusyReason);
    }

    public static bool MatchesWait(UnityInstanceSnapshot? selected, string waitFor, string? compilationCycleId,
        DateTimeOffset? observedAfterUtc)
    {
        if (selected == null)
            throw new BrokerOperationException(BrokerErrorCodes.ConnectionHandleRequired,
                "A connectionHandle or instanceId is required when waiting for Unity state.");

        if (string.Equals(waitFor, "ready", StringComparison.OrdinalIgnoreCase))
            return selected.IsConnected && IsAuthorized(selected) && CanExecute(selected.Status);

        if (string.Equals(waitFor, "compilation-complete", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(compilationCycleId) &&
                !string.Equals(selected.Status.CompilationCycleId, compilationCycleId, StringComparison.Ordinal))
                return false;
            if (observedAfterUtc.HasValue &&
                (!selected.Status.LastCompilationStartedAtUtc.HasValue ||
                 selected.Status.LastCompilationStartedAtUtc.Value < observedAfterUtc.Value))
                return false;
            return selected.IsConnected && IsAuthorized(selected) &&
                   (string.Equals(selected.Status.Phase, "Ready", StringComparison.Ordinal) ||
                    IsRepairMode(selected.Status));
        }

        throw new BrokerOperationException(BrokerErrorCodes.InvalidRequest,
            "waitFor must be snapshot, ready, or compilation-complete.");
    }
}
