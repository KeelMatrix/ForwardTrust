namespace KeelMatrix.ForwardTrust;

/// <summary>Describes whether the application is expected to apply or reject forwarded values.</summary>
public enum ForwardTrustHeaderExpectation
{
    /// <summary>The configured trusted boundary is expected to apply the declared forwarded values.</summary>
    Accepted = 0,

    /// <summary>The configured untrusted boundary is expected to leave forwarded values unapplied.</summary>
    Rejected = 1
}
