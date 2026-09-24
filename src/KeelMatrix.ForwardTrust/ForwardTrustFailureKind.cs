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

    /// <summary>The caller-provided host or probe did not return a usable identity.</summary>
    HostProbeFailure
}
