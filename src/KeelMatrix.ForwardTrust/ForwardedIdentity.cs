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
    /// <param name="host">The effective host. A null expected host omits the host assertion. Returning the observed host also enables original-commit controls when host is unasserted.</param>
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

    /// <summary>Gets the effective request scheme.</summary>
    public string Scheme { get; }

    /// <summary>Gets the effective host, or <see langword="null"/> to omit an expected host assertion or when the probe does not supply the observed host.</summary>
    public string? Host { get; }

    /// <summary>Gets a copy of the effective client address.</summary>
    public IPAddress ClientAddress => CopyAddress(clientAddress);

    private static IPAddress CopyAddress(IPAddress address)
    {
        var addressBytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPAddress(addressBytes, address.ScopeId)
            : new IPAddress(addressBytes);
    }
}
