using System.Collections.ObjectModel;

namespace KeelMatrix.ForwardTrust;

/// <summary>Describes one simulated peer request and its expected application-level interpretation.</summary>
public sealed class ForwardTrustScenario
{
    /// <summary>Creates a scenario. Address and header syntax are checked before its request is sent.</summary>
    public ForwardTrustScenario(
        string name,
        string immediatePeerAddress,
        ForwardedIdentity expectedIdentity,
        ForwardTrustHeaderExpectation headerExpectation,
        IReadOnlyDictionary<string, string>? headers = null,
        string path = "/forward-trust-probe")
    {
        Name = name ?? string.Empty;
        ImmediatePeerAddress = immediatePeerAddress ?? string.Empty;
        ExpectedIdentity = expectedIdentity ?? throw new ArgumentNullException(nameof(expectedIdentity));
        HeaderExpectation = headerExpectation;
        Path = path ?? string.Empty;
        Headers = new ReadOnlyDictionary<string, string>(
            headers is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Gets the stable scenario name used in diagnostics.</summary>
    public string Name { get; }

    /// <summary>Gets the simulated immediate peer address as supplied by the test author.</summary>
    public string ImmediatePeerAddress { get; }

    /// <summary>Gets the expected identity returned by the application probe.</summary>
    public ForwardedIdentity ExpectedIdentity { get; }

    /// <summary>Gets whether the scenario expects forwarded values to be applied or rejected.</summary>
    public ForwardTrustHeaderExpectation HeaderExpectation { get; }

    /// <summary>Gets the request path used by the caller-provided test host.</summary>
    public string Path { get; }

    /// <summary>Gets a defensive copy of request headers.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }
}
