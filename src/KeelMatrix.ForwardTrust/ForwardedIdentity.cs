using System.Net;

namespace KeelMatrix.ForwardTrust;

/// <summary>Represents the request identity observed by an application test probe.</summary>
public sealed record ForwardedIdentity
{
    /// <summary>Creates an observed or expected request identity.</summary>
    /// <param name="scheme">The effective request scheme, such as <c>http</c> or <c>https</c>.</param>
    /// <param name="clientAddress">The effective client address.</param>
    /// <param name="host">The effective host, when the scenario explicitly asserts it.</param>
    public ForwardedIdentity(string scheme, IPAddress clientAddress, string? host = null)
    {
        if (string.IsNullOrWhiteSpace(scheme))
        {
            throw new ArgumentException("A request scheme is required.", nameof(scheme));
        }

        ArgumentNullException.ThrowIfNull(clientAddress);
        Scheme = scheme.Trim();
        ClientAddress = clientAddress;
        Host = string.IsNullOrWhiteSpace(host) ? null : host.Trim();
    }

    /// <summary>Gets the effective request scheme.</summary>
    public string Scheme { get; }

    /// <summary>Gets the explicitly asserted effective host, or <see langword="null"/> when host is not asserted.</summary>
    public string? Host { get; }

    /// <summary>Gets the effective client address.</summary>
    public IPAddress ClientAddress { get; }
}
