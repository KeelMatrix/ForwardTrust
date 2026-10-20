using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;

namespace KeelMatrix.ForwardTrust;

/// <summary>Describes the request inputs a caller-provided test host must apply.</summary>
public sealed class ForwardTrustRequest
{
    private readonly IPAddress immediatePeerAddress;

    internal ForwardTrustRequest(
        ForwardTrustScenario scenario,
        IPAddress immediatePeerAddress,
        IReadOnlyDictionary<string, string> headers)
    {
        Scenario = scenario;
        this.immediatePeerAddress = CopyAddress(immediatePeerAddress);
        var requestHeaders = ForwardTrustScenario.CopyHeadersBounded(
            headers,
            out var limitsExceeded,
            out var duplicateHeaderNames);
        Headers = new ReadOnlyDictionary<string, string>(requestHeaders);
        if (limitsExceeded || duplicateHeaderNames)
        {
            throw new InvalidOperationException("A validated request exceeded the bounded header contract.");
        }
        Path = scenario.Path;
    }

    /// <summary>Gets the scenario that produced this request.</summary>
    public ForwardTrustScenario Scenario { get; }

    /// <summary>Gets the request path to send to the test host.</summary>
    public string Path { get; }

    /// <summary>Gets a defensive copy of the simulated immediate peer address to assign to the test connection.</summary>
    public IPAddress ImmediatePeerAddress => CopyAddress(immediatePeerAddress);

    /// <summary>Gets the headers the test host should apply to the request.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    private static IPAddress CopyAddress(IPAddress address)
    {
        var addressBytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPAddress(addressBytes, address.ScopeId)
            : new IPAddress(addressBytes);
    }
}
