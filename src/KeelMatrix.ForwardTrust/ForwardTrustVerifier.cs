using System.Collections.ObjectModel;
using System.Net;

namespace KeelMatrix.ForwardTrust;

/// <summary>Executes immutable forwarded-header scenarios against a caller-provided test host.</summary>
public sealed class ForwardTrustVerifier
{
    private const int MaxNameLength = 128;
    private const int MaxPathLength = 2048;
    private const int MaxHeaderLength = 16 * 1024;
    private readonly ForwardTrustVerifierOptions options;

    /// <summary>Creates a verifier with conservative finite limits.</summary>
    public ForwardTrustVerifier(ForwardTrustVerifierOptions? options = null)
    {
        this.options = options ?? new ForwardTrustVerifierOptions();
        if (this.options.MaxScenarioCount is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxScenarioCount must be between 1 and 256.");
        }

        if (this.options.RequestTimeout <= TimeSpan.Zero || this.options.RequestTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RequestTimeout must be positive and no longer than two minutes.");
        }
    }

    /// <summary>Verifies scenarios asynchronously without changing the caller's ASP.NET Core configuration.</summary>
    /// <param name="scenarios">The finite scenario sequence to validate and execute.</param>
    /// <param name="requestSender">The caller-owned request/probe seam.</param>
    /// <param name="cancellationToken">The cancellation token for the verification run.</param>
    public async Task<ForwardTrustResult> VerifyAsync(
        IEnumerable<ForwardTrustScenario> scenarios,
        ForwardTrustRequestSender requestSender,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(requestSender);

        var scenarioList = scenarios.ToArray();
        if (scenarioList.Length > options.MaxScenarioCount)
        {
            return CreateSingleFailureResult(
                "verification",
                ForwardTrustFailureKind.MalformedScenario,
                ForwardTrustDimension.Scenario,
                $"The scenario set contains {scenarioList.Length} entries, exceeding the configured maximum of {options.MaxScenarioCount}.");
        }

        var validationFailures = scenarioList
            .Select(ValidateScenario)
            .ToArray();

        var results = new List<ForwardTrustScenarioResult>(scenarioList.Length);
        for (var index = 0; index < scenarioList.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scenario = scenarioList[index];
            var scenarioFailures = validationFailures[index];
            if (scenarioFailures.Count > 0)
            {
                results.Add(new ForwardTrustScenarioResult(scenario?.Name ?? "(unnamed)", false, null, scenarioFailures));
                continue;
            }

            results.Add(await VerifyScenarioAsync(scenario!, requestSender, cancellationToken).ConfigureAwait(false));
        }

        return new ForwardTrustResult(new ReadOnlyCollection<ForwardTrustScenarioResult>(results));
    }

    private async Task<ForwardTrustScenarioResult> VerifyScenarioAsync(
        ForwardTrustScenario scenario,
        ForwardTrustRequestSender requestSender,
        CancellationToken cancellationToken)
    {
        var peerAddress = IPAddress.Parse(scenario.ImmediatePeerAddress);
        var request = new ForwardTrustRequest(scenario, peerAddress);
        ForwardedIdentity? observedIdentity;

        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(options.RequestTimeout);
            observedIdentity = await requestSender(request, timeoutSource.Token)
                .AsTask()
                .WaitAsync(timeoutSource.Token)
                .ConfigureAwait(false);
            if (observedIdentity is null)
            {
                return CreateProbeFailure(scenario.Name, "The test host returned no request identity.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return CreateProbeFailure(scenario.Name, "The test host or probe exceeded the finite request timeout.");
        }
        catch
        {
            return CreateProbeFailure(scenario.Name, "The test host or probe failed before it returned a request identity.");
        }

        var failures = CompareIdentity(scenario, observedIdentity);
        return new ForwardTrustScenarioResult(scenario.Name, failures.Count == 0, observedIdentity, failures);
    }

    private static IReadOnlyList<ForwardTrustFailure> ValidateScenario(ForwardTrustScenario? scenario)
    {
        if (scenario is null)
        {
            return [new ForwardTrustFailure(
                "(unnamed)",
                ForwardTrustFailureKind.MalformedScenario,
                ForwardTrustDimension.Scenario,
                "The scenario is null and cannot be sent.")];
        }

        var failures = new List<ForwardTrustFailure>();
        if (string.IsNullOrWhiteSpace(scenario.Name) || scenario.Name.Length > MaxNameLength)
        {
            failures.Add(Malformed(scenario, "Scenario names must be non-empty and no longer than 128 characters."));
        }

        if (!IPAddress.TryParse(scenario.ImmediatePeerAddress, out _))
        {
            failures.Add(Malformed(scenario, "The simulated immediate peer address is not a valid IP address."));
        }

        if (string.IsNullOrWhiteSpace(scenario.Path)
            || scenario.Path.Length > MaxPathLength
            || !scenario.Path.StartsWith('/')
            || scenario.Path.Contains('\r')
            || scenario.Path.Contains('\n'))
        {
            failures.Add(Malformed(scenario, "The probe path must be an absolute application path without line breaks."));
        }

        foreach (var header in scenario.Headers)
        {
            if (string.IsNullOrWhiteSpace(header.Key)
                || header.Key.Length > 256
                || header.Key.Any(char.IsWhiteSpace)
                || header.Key.Contains(':')
                || header.Key.Contains('\r')
                || header.Key.Contains('\n')
                || header.Value is null
                || header.Value.Length > MaxHeaderLength
                || header.Value.Contains('\r')
                || header.Value.Contains('\n'))
            {
                failures.Add(Malformed(scenario, "A header name or value is invalid or exceeds the bounded request limit."));
                break;
            }
        }

        return failures;
    }

    private static List<ForwardTrustFailure> CompareIdentity(
        ForwardTrustScenario scenario,
        ForwardedIdentity observed)
    {
        var failures = new List<ForwardTrustFailure>();
        var expected = scenario.ExpectedIdentity;
        var schemeMatches = string.Equals(expected.Scheme, observed.Scheme, StringComparison.OrdinalIgnoreCase);
        var hostMatches = expected.Host is null
            || string.Equals(expected.Host, observed.Host, StringComparison.OrdinalIgnoreCase);
        var addressMatches = expected.ClientAddress.MapToIPv6().Equals(observed.ClientAddress.MapToIPv6());

        if (!schemeMatches)
        {
            failures.Add(new ForwardTrustFailure(
                scenario.Name,
                ForwardTrustFailureKind.SchemeMismatch,
                ForwardTrustDimension.Scheme,
                "The effective scheme did not match the scenario. Check forwarded-header trust and middleware placement.",
                expected.Scheme,
                observed.Scheme));
        }

        if (!hostMatches)
        {
            failures.Add(new ForwardTrustFailure(
                scenario.Name,
                ForwardTrustFailureKind.HostMismatch,
                ForwardTrustDimension.Host,
                "The explicitly asserted host did not match. Check allowed hosts, trusted proxy configuration, and middleware placement.",
                expected.Host,
                observed.Host));
        }

        if (!addressMatches)
        {
            failures.Add(new ForwardTrustFailure(
                scenario.Name,
                ForwardTrustFailureKind.ClientAddressMismatch,
                ForwardTrustDimension.ClientAddress,
                "The effective client address did not match. Check trusted proxy/network configuration and middleware placement.",
                expected.ClientAddress.ToString(),
                observed.ClientAddress.ToString()));
        }

        var hasForwardedHeaders = scenario.Headers.Keys.Any(IsForwardedHeader);
        var forwardedValueObserved = ObservedForwardedValue(scenario.Headers, observed);
        if (scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Accepted)
        {
            if (hasForwardedHeaders && !forwardedValueObserved)
            {
                failures.Add(new ForwardTrustFailure(
                    scenario.Name,
                    ForwardTrustFailureKind.TrustedHeaderRejected,
                    ForwardTrustDimension.Headers,
                    "The trusted scenario's forwarded values were not applied. Check trusted proxy/network configuration and middleware placement."));
            }

            if (GetHopCount(scenario.Headers) > 1 && failures.Any(static failure =>
                    failure.Kind is ForwardTrustFailureKind.ClientAddressMismatch or ForwardTrustFailureKind.SchemeMismatch or ForwardTrustFailureKind.HostMismatch))
            {
                failures.Add(new ForwardTrustFailure(
                    scenario.Name,
                    ForwardTrustFailureKind.ForwardLimitMismatch,
                    ForwardTrustDimension.ForwardLimit,
                    "The multi-hop forwarded values did not produce the expected identity. Check ForwardLimit and trusted proxy/network configuration."));
            }
        }
        else if (hasForwardedHeaders && forwardedValueObserved)
        {
            failures.Add(new ForwardTrustFailure(
                scenario.Name,
                ForwardTrustFailureKind.UntrustedHeaderAccepted,
                ForwardTrustDimension.Headers,
                "The untrusted scenario's forwarded values were applied. Check trusted proxy/network configuration and middleware placement; do not broaden trust as a convenience fix."));
        }

        return failures;
    }

    private static bool ObservedForwardedValue(
        IReadOnlyDictionary<string, string> headers,
        ForwardedIdentity observed)
    {
        if (TryGetHeader(headers, "X-Forwarded-Proto", out var proto)
            && SplitHeaderValues(proto).Any(value => string.Equals(value, observed.Scheme, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (observed.Host is not null
            && TryGetHeader(headers, "X-Forwarded-Host", out var host)
            && SplitHeaderValues(host).Any(value => string.Equals(value, observed.Host, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (TryGetHeader(headers, "X-Forwarded-For", out var forwardedFor)
            && SplitHeaderValues(forwardedFor).Any(value => IPAddress.TryParse(value, out var ip)
                && ip.MapToIPv6().Equals(observed.ClientAddress.MapToIPv6())))
        {
            return true;
        }

        return false;
    }

    private static int GetHopCount(IReadOnlyDictionary<string, string> headers)
    {
        return headers
            .Where(static header => header.Key.Equals("X-Forwarded-For", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("X-Forwarded-Proto", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("X-Forwarded-Host", StringComparison.OrdinalIgnoreCase))
            .SelectMany(static header => SplitHeaderValues(header.Value))
            .Count();
    }

    private static string[] SplitHeaderValues(string value)
    {
        return value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool IsForwardedHeader(string name)
    {
        return name.Equals("X-Forwarded-For", StringComparison.OrdinalIgnoreCase)
            || name.Equals("X-Forwarded-Proto", StringComparison.OrdinalIgnoreCase)
            || name.Equals("X-Forwarded-Host", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetHeader(IReadOnlyDictionary<string, string> headers, string name, out string value)
    {
        return headers.TryGetValue(name, out value!);
    }

    private static ForwardTrustFailure Malformed(ForwardTrustScenario scenario, string message)
    {
        return new ForwardTrustFailure(scenario.Name, ForwardTrustFailureKind.MalformedScenario, ForwardTrustDimension.Scenario, message);
    }

    private static ForwardTrustScenarioResult CreateProbeFailure(string scenarioName, string message)
    {
        var failure = new ForwardTrustFailure(scenarioName, ForwardTrustFailureKind.HostProbeFailure, ForwardTrustDimension.Scenario, message);
        return new ForwardTrustScenarioResult(scenarioName, false, null, [failure]);
    }

    private static ForwardTrustResult CreateSingleFailureResult(
        string scenarioName,
        ForwardTrustFailureKind kind,
        ForwardTrustDimension dimension,
        string message)
    {
        var failure = new ForwardTrustFailure(scenarioName, kind, dimension, message);
        var scenario = new ForwardTrustScenarioResult(scenarioName, false, null, [failure]);
        return new ForwardTrustResult([scenario]);
    }
}
