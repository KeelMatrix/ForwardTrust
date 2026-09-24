namespace KeelMatrix.ForwardTrust;

/// <summary>Represents a caller-owned asynchronous request/probe seam for a test host.</summary>
/// <param name="request">The request and simulated immediate peer to apply.</param>
/// <param name="cancellationToken">The request cancellation token.</param>
/// <returns>The identity observed by the caller's application probe.</returns>
public delegate ValueTask<ForwardedIdentity> ForwardTrustRequestSender(
    ForwardTrustRequest request,
    CancellationToken cancellationToken);
