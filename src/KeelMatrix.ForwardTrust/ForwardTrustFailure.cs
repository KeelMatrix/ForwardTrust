namespace KeelMatrix.ForwardTrust;

/// <summary>Structured, redaction-safe information about one failed assertion.</summary>
public sealed record ForwardTrustFailure
{
    internal ForwardTrustFailure(
        string scenarioName,
        ForwardTrustFailureKind kind,
        ForwardTrustDimension dimension,
        string message,
        string? expectedValue = null,
        string? observedValue = null)
    {
        ScenarioName = scenarioName;
        Kind = kind;
        Dimension = dimension;
        Message = message;
        ExpectedValue = expectedValue;
        ObservedValue = observedValue;
    }

    /// <summary>Gets the scenario name.</summary>
    public string ScenarioName { get; }

    /// <summary>Gets the failure category.</summary>
    public ForwardTrustFailureKind Kind { get; }

    /// <summary>Gets the trust dimension involved.</summary>
    public ForwardTrustDimension Dimension { get; }

    /// <summary>Gets a bounded diagnostic that does not include request bodies or unrelated headers.</summary>
    public string Message { get; }

    /// <summary>Gets the expected scalar value for a dimension, when useful.</summary>
    public string? ExpectedValue { get; }

    /// <summary>Gets the observed scalar value for a dimension, when useful.</summary>
    public string? ObservedValue { get; }
}
