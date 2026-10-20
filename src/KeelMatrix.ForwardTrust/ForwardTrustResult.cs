namespace KeelMatrix.ForwardTrust;

/// <summary>Contains the ordered result of a verification run.</summary>
public sealed class ForwardTrustResult
{
    internal ForwardTrustResult(IReadOnlyList<ForwardTrustScenarioResult> scenarios)
    {
        Scenarios = Array.AsReadOnly(scenarios.ToArray());
        Failures = Array.AsReadOnly(Scenarios.SelectMany(static scenario => scenario.Failures).ToArray());
    }

    /// <summary>Gets a read-only snapshot of the per-scenario results.</summary>
    public IReadOnlyList<ForwardTrustScenarioResult> Scenarios { get; }

    /// <summary>Gets a read-only snapshot of all failures in scenario order.</summary>
    public IReadOnlyList<ForwardTrustFailure> Failures { get; }

    /// <summary>Gets whether every declared scenario passed.</summary>
    public bool Succeeded => Failures.Count == 0;
}
