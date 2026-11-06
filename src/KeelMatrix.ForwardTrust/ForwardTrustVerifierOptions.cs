namespace KeelMatrix.ForwardTrust;

/// <summary>Controls finite verifier limits.</summary>
public sealed class ForwardTrustVerifierOptions
{
    /// <summary>Maximum number of scenarios accepted by one verification call.</summary>
    public int MaxScenarioCount { get; init; } = 32;

    /// <summary>Maximum time allowed for each caller-provided host/probe request. Defaults to five seconds.</summary>
    /// <remarks>
    /// Must be greater than zero and no greater than two minutes. Constructing a
    /// <see cref="ForwardTrustVerifier"/> with zero, a negative value, or a value above two minutes throws
    /// <see cref="ArgumentOutOfRangeException"/>. If a host/probe request exceeds this timeout, verification
    /// reports a <see cref="ForwardTrustFailureKind.HostProbeFailure"/> for that scenario.
    /// The deadline includes synchronous sender work before it returns a task and asynchronous completion.
    /// Results returned after the deadline are rejected. Arbitrary caller code cannot be forcibly interrupted;
    /// a synchronously blocking sender is checked when it returns. Caller cancellation is checked before
    /// each invocation and after completion, and propagates as <see cref="OperationCanceledException"/>.
    /// </remarks>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
