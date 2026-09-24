using System.Collections.ObjectModel;
using System.Net;

namespace KeelMatrix.ForwardTrust;

/// <summary>Describes the request inputs a caller-provided test host must apply.</summary>
public sealed class ForwardTrustRequest
{
    internal ForwardTrustRequest(ForwardTrustScenario scenario, IPAddress immediatePeerAddress)
    {
        Scenario = scenario;
        ImmediatePeerAddress = immediatePeerAddress;
        Headers = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(scenario.Headers, StringComparer.OrdinalIgnoreCase));
        Path = scenario.Path;
    }

    /// <summary>Gets the scenario that produced this request.</summary>
    public ForwardTrustScenario Scenario { get; }

    /// <summary>Gets the request path to send to the test host.</summary>
    public string Path { get; }

    /// <summary>Gets the simulated immediate peer address to assign to the test connection.</summary>
    public IPAddress ImmediatePeerAddress { get; }

    /// <summary>Gets the headers the test host should apply to the request.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }
}
