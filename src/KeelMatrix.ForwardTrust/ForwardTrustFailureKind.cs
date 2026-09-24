namespace KeelMatrix.ForwardTrust;

/// <summary>Classifies a failed trust assertion without exposing request contents.</summary>
public enum ForwardTrustFailureKind
{
    /// <summary>Forwarded values expected from a trusted proxy were not applied.</summary>
    TrustedHeaderRejected,

    /// <summary>Forwarded values from an untrusted proxy were applied.</summary>
    UntrustedHeaderAccepted,

    /// <summary>The effective scheme differed from the expectation.</summary>
    SchemeMismatch,

    /// <summary>The effective host differed from an explicitly asserted expectation.</summary>
    HostMismatch,

    /// <summary>The effective client address differed from the expectation.</summary>
    ClientAddressMismatch,

    /// <summary>A multi-hop assertion did not produce the expected effective identity.</summary>
    ForwardLimitMismatch,

    /// <summary>The scenario was invalid and no request was sent.</summary>
    MalformedScenario,

    /// <summary>The caller-provided host or probe exceeded the finite request timeout.</summary>
    HostProbeFailure,

    /// <summary>The caller-provided host or probe failed before it returned an identity.</summary>
    HostSetupFailure,

    /// <summary>The request sender did not prove that it applied the request and peer seam.</summary>
    RequestApplicationNotProven,

    /// <summary>A forwarded value matched, but its effect was not proven by a counterfactual request.</summary>
    ForwardedValueNotProven
}
