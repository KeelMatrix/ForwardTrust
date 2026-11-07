using System.Net;
using System.Net.Sockets;

namespace KeelMatrix.ForwardTrust;

/// <summary>Represents the request identity observed by an application test probe.</summary>
public sealed record ForwardedIdentity
{
    private readonly IPAddress clientAddress;

    /// <summary>Creates an observed or expected request identity.</summary>
    /// <param name="scheme">The effective request scheme, such as <c>http</c> or <c>https</c>.</param>
    /// <param name="clientAddress">The effective client address, copied when this identity is created.</param>
    /// <param name="host">The effective host. A null expected host omits the host assertion.</param>
    public ForwardedIdentity(string scheme, IPAddress clientAddress, string? host = null)
    {
        if (string.IsNullOrWhiteSpace(scheme))
        {
            throw new ArgumentException("A request scheme is required.", nameof(scheme));
        }

        ArgumentNullException.ThrowIfNull(clientAddress);
        Scheme = scheme.Trim();
        this.clientAddress = CopyAddress(clientAddress);
        Host = string.IsNullOrWhiteSpace(host) ? null : host.Trim();
    }

    /// <summary>Returns a copy with an observation of the remaining conventional X-Forwarded-For header.</summary>
    /// <remarks>
    /// Capture this header at the same probe point as the identity, after this exact request traverses
    /// the application pipeline. Pass null only when capture occurred and the header was absent.
    /// Omit this call when capture is unavailable. Do not reconstruct the value
    /// from expectations. This observation is not an assertion and is ignored on expected identities.
    /// </remarks>
    /// <param name="remainingForwardedFor">The observed remaining header value, or null for an observed absent header. Limited to 16,384 characters.</param>
    /// <exception cref="ArgumentException">The observation exceeds its finite size bound.</exception>
    /// <returns>An immutable identity snapshot with capture explicitly available.</returns>
    public ForwardedIdentity WithForwardedForObservation(string? remainingForwardedFor)
    {
        if (remainingForwardedFor?.Length > ForwardTrustScenario.MaxHeaderValueLength)
        {
            throw new ArgumentException("The remaining forwarded-address observation exceeds the bounded value limit.", nameof(remainingForwardedFor));
        }
        return this with { HasForwardedForObservation = true, RemainingForwardedFor = remainingForwardedFor };
    }

    /// <summary>Gets the effective request scheme.</summary>
    public string Scheme { get; }

    /// <summary>Gets the effective host, or <see langword="null"/> to omit an expected host assertion or when the probe does not supply the observed host.</summary>
    public string? Host { get; }

    /// <summary>Gets a copy of the effective client address.</summary>
    public IPAddress ClientAddress => CopyAddress(clientAddress);

    /// <summary>Gets whether the sender captured the remaining conventional X-Forwarded-For header at the identity probe point.</summary>
    public bool HasForwardedForObservation { get; private init; }

    /// <summary>Gets the immutable remaining conventional X-Forwarded-For value, or null for an observed absent header when <see cref="HasForwardedForObservation"/> is true.</summary>
    /// <remarks>When capture is unavailable, this property is also null and <see cref="HasForwardedForObservation"/> is false. Endpoint tokens, ports and order are preserved; this is not another asserted identity dimension.</remarks>
    public string? RemainingForwardedFor { get; private init; }

    private static IPAddress CopyAddress(IPAddress address)
    {
        var addressBytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPAddress(addressBytes, address.ScopeId)
            : new IPAddress(addressBytes);
    }
}
