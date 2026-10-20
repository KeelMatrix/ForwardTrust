namespace KeelMatrix.ForwardTrust;

/// <summary>Identifies the trust dimension involved in a failure.</summary>
public enum ForwardTrustDimension
{
    /// <summary>The complete forwarded-header trust boundary.</summary>
    Headers,

    /// <summary>The forwarded scheme.</summary>
    Scheme,

    /// <summary>The forwarded host.</summary>
    Host,

    /// <summary>The effective client address.</summary>
    ClientAddress,

    /// <summary>The configured number of forwarded hops.</summary>
    ForwardLimit,

    /// <summary>The scenario or caller-provided host setup.</summary>
    Scenario
}
