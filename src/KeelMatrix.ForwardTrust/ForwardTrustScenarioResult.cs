namespace KeelMatrix.ForwardTrust;

/// <summary>Contains the verdict and bounded diagnostics for one scenario.</summary>
public sealed class ForwardTrustScenarioResult
{
    internal ForwardTrustScenarioResult(
        string scenarioName,
        bool succeeded,
        ForwardedIdentity? observedIdentity,
        IReadOnlyList<ForwardTrustFailure> failures)
    {
        ScenarioName = scenarioName;
        Succeeded = succeeded;
        ObservedIdentity = observedIdentity;
        Failures = failures;
    }

    /// <summary>Gets the scenario name.</summary>
    public string ScenarioName { get; }

    /// <summary>Gets whether all declared assertions passed.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the probe identity, or <see langword="null"/> when the host failed.</summary>
    public ForwardedIdentity? ObservedIdentity { get; }

    /// <summary>Gets structured failures for the scenario.</summary>
    public IReadOnlyList<ForwardTrustFailure> Failures { get; }
}
