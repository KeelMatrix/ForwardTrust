namespace KeelMatrix.ForwardTrust;

/// <summary>Controls finite, deterministic verifier limits.</summary>
public sealed class ForwardTrustVerifierOptions
{
    /// <summary>Maximum number of scenarios accepted by one verification call.</summary>
    public int MaxScenarioCount { get; init; } = 32;

    /// <summary>Maximum time allowed for each caller-provided host/probe request.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
