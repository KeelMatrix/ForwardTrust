namespace KeelMatrix.ForwardTrust;

/// <summary>Represents a caller-owned asynchronous seam that executes requests against a test host.</summary>
/// <param name="request">The request and simulated immediate peer to apply to the application pipeline.</param>
/// <param name="cancellationToken">The linked caller/deadline token. Observe it cooperatively; the verifier cannot forcibly interrupt synchronous caller code and rejects late results when control returns.</param>
/// <returns>The identity observed after the application pipeline executes the request.</returns>
public delegate ValueTask<ForwardedIdentity> ForwardTrustRequestSender(
    ForwardTrustRequest request,
    CancellationToken cancellationToken);
