namespace KeelMatrix.ForwardTrust;

/// <summary>Defines the opposite-trust peer observation used as evidence for one scenario.</summary>
public sealed class ForwardTrustControl
{
    /// <summary>Creates an opposite-trust control for the scenario's declared headers.</summary>
    /// <param name="immediatePeerAddress">The simulated peer expected to produce the opposite header-handling outcome.</param>
    /// <param name="expectedIdentity">The identity expected when the same headers are sent through that peer.</param>
    public ForwardTrustControl(string immediatePeerAddress, ForwardedIdentity expectedIdentity)
    {
        ImmediatePeerAddress = immediatePeerAddress ?? string.Empty;
        ExpectedIdentity = expectedIdentity ?? throw new ArgumentNullException(nameof(expectedIdentity));
    }

    /// <summary>Gets the simulated peer address for the opposite-trust control.</summary>
    public string ImmediatePeerAddress { get; }

    /// <summary>Gets the identity expected from the opposite-trust control request.</summary>
    public ForwardedIdentity ExpectedIdentity { get; }
}
