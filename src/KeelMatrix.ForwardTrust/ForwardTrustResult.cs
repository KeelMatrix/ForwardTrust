namespace KeelMatrix.ForwardTrust;

/// <summary>Contains the ordered result of a verification run.</summary>
public sealed class ForwardTrustResult
{
    internal ForwardTrustResult(IReadOnlyList<ForwardTrustScenarioResult> scenarios)
    {
        Scenarios = scenarios;
        Failures = scenarios.SelectMany(static scenario => scenario.Failures).ToArray();
    }

    /// <summary>Gets the per-scenario results.</summary>
    public IReadOnlyList<ForwardTrustScenarioResult> Scenarios { get; }

    /// <summary>Gets all failures in scenario order.</summary>
    public IReadOnlyList<ForwardTrustFailure> Failures { get; }

    /// <summary>Gets whether every declared scenario passed.</summary>
    public bool Succeeded => Failures.Count == 0;
}
