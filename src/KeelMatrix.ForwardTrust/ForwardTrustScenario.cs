using System.Collections.ObjectModel;

namespace KeelMatrix.ForwardTrust;

/// <summary>Describes one simulated peer request and its expected application-level interpretation.</summary>
public sealed class ForwardTrustScenario
{
    internal const int MaxHeaderCount = 64;
    internal const int MaxHeaderNameLength = 256;
    internal const int MaxHeaderValueLength = 16 * 1024;
    internal const int MaxAggregateHeaderBytes = 64 * 1024;

    /// <summary>Creates a scenario. Address, control, and header syntax are checked before any request is sent.</summary>
    /// <param name="name">The stable scenario name used in diagnostics.</param>
    /// <param name="immediatePeerAddress">The simulated peer address for the primary request.</param>
    /// <param name="expectedIdentity">The expected identity for the primary request.</param>
    /// <param name="headerExpectation">Whether the primary peer should accept or reject forwarded values.</param>
    /// <param name="headers">The request headers to apply.</param>
    /// <param name="path">The application probe path.</param>
    /// <param name="control">The opposite-trust peer and expected identity required when a forwarded dimension is asserted.</param>
    public ForwardTrustScenario(
        string name,
        string immediatePeerAddress,
        ForwardedIdentity expectedIdentity,
        ForwardTrustHeaderExpectation headerExpectation,
        IReadOnlyDictionary<string, string>? headers = null,
        string path = "/forward-trust-probe",
        ForwardTrustControl? control = null)
    {
        Name = name ?? string.Empty;
        ImmediatePeerAddress = immediatePeerAddress ?? string.Empty;
        ExpectedIdentity = expectedIdentity ?? throw new ArgumentNullException(nameof(expectedIdentity));
        HeaderExpectation = headerExpectation;
        Control = control;
        Path = path ?? string.Empty;
        Headers = new ReadOnlyDictionary<string, string>(CopyHeadersBounded(headers, out var headerLimitsExceeded));
        HeaderLimitsExceeded = headerLimitsExceeded;
    }

    /// <summary>Gets the stable scenario name used in diagnostics.</summary>
    public string Name { get; }

    /// <summary>Gets the simulated immediate peer address as supplied by the test author.</summary>
    public string ImmediatePeerAddress { get; }

    /// <summary>Gets the expected identity returned by the application probe.</summary>
    public ForwardedIdentity ExpectedIdentity { get; }

    /// <summary>Gets whether the scenario expects forwarded values to be applied or rejected.</summary>
    public ForwardTrustHeaderExpectation HeaderExpectation { get; }

    /// <summary>Gets the scenario-local opposite-trust control, or <see langword="null"/> when no forwarded dimension is asserted.</summary>
    public ForwardTrustControl? Control { get; }

    /// <summary>Gets the request path used by the caller-provided test host.</summary>
    public string Path { get; }

    /// <summary>Gets a defensive copy of request headers.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    internal bool HeaderLimitsExceeded { get; }

    internal static Dictionary<string, string> CopyHeadersBounded(
        IReadOnlyDictionary<string, string>? headers,
        out bool limitsExceeded)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        limitsExceeded = headers is not null && headers.Count > MaxHeaderCount;
        if (headers is null || limitsExceeded)
        {
            return copy;
        }

        var headerCount = 0;
        var aggregateLength = 0L;
        foreach (var header in headers)
        {
            headerCount++;
            if (headerCount > MaxHeaderCount)
            {
                limitsExceeded = true;
                break;
            }

            if (header.Key is null || header.Value is null)
            {
                limitsExceeded = true;
                break;
            }

            if (header.Key.Length > MaxHeaderNameLength || header.Value.Length > MaxHeaderValueLength)
            {
                limitsExceeded = true;
                break;
            }

            aggregateLength += System.Text.Encoding.UTF8.GetByteCount(header.Key);
            aggregateLength += System.Text.Encoding.UTF8.GetByteCount(header.Value);
            if (aggregateLength > MaxAggregateHeaderBytes)
            {
                limitsExceeded = true;
                break;
            }

            copy[header.Key] = header.Value;
        }

        return copy;
    }
}
