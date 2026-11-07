namespace KeelMatrix.ForwardTrust;

/// <summary>Describes whether the application is expected to apply or reject forwarded values.</summary>
public enum ForwardTrustHeaderExpectation
{
    /// <summary>The configured trusted boundary is expected to apply the original declared values at the effective consumed hop of each asserted dimension.</summary>
    /// <remarks>Original application must be distinguishable from the accepted path's own baseline. A changed forwarded address can stop trust traversal and commit when the original request aborted. A coincident address therefore requires an observable original scheme or forwarded-host effect; otherwise it remains unproven. Host assertions remain optional. A permitted alternate host alone cannot prove original host application.</remarks>
    Accepted = 0,

    /// <summary>The configured untrusted boundary is expected to leave forwarded values unapplied.</summary>
    Rejected = 1
}
