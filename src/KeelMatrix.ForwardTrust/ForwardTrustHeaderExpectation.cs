namespace KeelMatrix.ForwardTrust;

/// <summary>Describes whether the application is expected to apply or reject forwarded values.</summary>
public enum ForwardTrustHeaderExpectation
{
    /// <summary>The configured trusted boundary is expected to apply the original declared values at the effective consumed hop of each asserted dimension.</summary>
    /// <remarks>
    /// Original address application requires repeatable dimension-specific evidence. A sender can
    /// capture remaining conventional X-Forwarded-For with <see cref="ForwardedIdentity.WithForwardedForObservation"/>
    /// at the identity probe point. The unchanged original and replay must show removal of a valid
    /// endpoint suffix, leaving exactly its untouched logical prefix. Its boundary address must
    /// match the observed and expected address, and the causal hop trial must use that same index.
    /// Framework comma/quote semantics apply; residual tokens retain ports, representation and order.
    /// A malformed earlier prefix can remain, but inconsistent evidence fails closed. Observed
    /// absence is distinct from unavailable capture; rejected originals must retain their chain.
    /// Without capture, an original address must differ from the replayed accepted baseline that
    /// omits only X-Forwarded-For and retains other original headers. Coincident identity-only
    /// results remain unproven. A separate scheme, Host or changed-request commit cannot prove
    /// original address application. Exact alternate-value, opposite-peer, replay and dependency
    /// checks remain required. Host assertions remain optional. A permitted alternate host alone
    /// cannot prove original host application. This bounded observation assumes a faithful sender
    /// and conventional headers; arbitrary header removal/reconstruction or later identity rewrites
    /// cannot silently inherit this proof.
    /// </remarks>
    Accepted = 0,

    /// <summary>The configured untrusted boundary is expected to leave forwarded values unapplied.</summary>
    Rejected = 1
}
