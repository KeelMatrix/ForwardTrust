using System.Collections.ObjectModel;
using System.Net;

namespace KeelMatrix.ForwardTrust;

/// <summary>Executes immutable forwarded-header scenarios against a caller-provided test host.</summary>
public sealed class ForwardTrustVerifier
{
    private const int MaxNameLength = 128;
    private const int MaxPathLength = 2048;
    private const int MaxHeaderLength = 16 * 1024;
    private static readonly IPAddress ControlPeerOne = IPAddress.Parse("192.0.2.253");
    private static readonly IPAddress ControlPeerTwo = IPAddress.Parse("192.0.2.254");
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

        var scenarioList = scenarios.Cast<ForwardTrustScenario?>().ToArray();
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
            .Select(static failures => failures.ToList())
            .ToArray();
        AddScenarioSetValidationFailures(scenarioList, validationFailures);

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
        var primaryAttempt = await SendAsync(scenario, requestSender, cancellationToken).ConfigureAwait(false);
        if (primaryAttempt.Failure is not null)
        {
            return new ForwardTrustScenarioResult(scenario.Name, false, null, [primaryAttempt.Failure]);
        }

        var observedIdentity = primaryAttempt.Identity!;
        var failures = CompareIdentity(scenario, observedIdentity);
        var controlFailures = await VerifyRequestApplicationAsync(scenario, requestSender, cancellationToken).ConfigureAwait(false);
        failures.AddRange(controlFailures);

        if (controlFailures.Count == 0)
        {
            foreach (var dimension in GetAssertedDimensions(scenario))
            {
                var counterfactual = CreateCounterfactualScenario(scenario, dimension.HeaderName);
                var counterfactualAttempt = await SendAsync(counterfactual, requestSender, cancellationToken).ConfigureAwait(false);
                if (counterfactualAttempt.Failure is not null)
                {
                    failures.Add(counterfactualAttempt.Failure);
                    continue;
                }

                var dimensionMatches = SameDimension(dimension.Dimension, observedIdentity, counterfactualAttempt.Identity!);
                if (scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Accepted && dimensionMatches)
                {
                    failures.Add(new ForwardTrustFailure(
                        scenario.Name,
                        ForwardTrustFailureKind.ForwardedValueNotProven,
                        dimension.Dimension,
                        "The forwarded value matched, but changing that header did not change the observed dimension. The result is not proven to be caused by forwarded-header handling."));
                }
                else if (scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Rejected && !dimensionMatches)
                {
                    failures.Add(new ForwardTrustFailure(
                        scenario.Name,
                        ForwardTrustFailureKind.UntrustedHeaderAccepted,
                        dimension.Dimension,
                        "Changing the untrusted forwarded value changed the observed dimension. Check trusted proxy/network configuration and middleware placement; do not broaden trust as a convenience fix."));
                }
            }
        }

        if (scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Accepted
            && GetAssertedDimensions(scenario).Count == 0)
        {
            failures.Add(new ForwardTrustFailure(
                scenario.Name,
                ForwardTrustFailureKind.ForwardedValueNotProven,
                ForwardTrustDimension.Headers,
                "The accepted scenario did not assert a forwarded scheme, host, or client address, so header handling cannot be proven."));
        }

        return new ForwardTrustScenarioResult(scenario.Name, failures.Count == 0, observedIdentity, failures);
    }

    private async Task<IReadOnlyList<ForwardTrustFailure>> VerifyRequestApplicationAsync(
        ForwardTrustScenario scenario,
        ForwardTrustRequestSender requestSender,
        CancellationToken cancellationToken)
    {
        var failures = new List<ForwardTrustFailure>();
        var controlScenarios = new[]
        {
            CreatePeerControlScenario(scenario, ControlPeerOne),
            CreatePeerControlScenario(scenario, ControlPeerTwo)
        };

        foreach (var controlScenario in controlScenarios)
        {
            var attempt = await SendAsync(controlScenario, requestSender, cancellationToken).ConfigureAwait(false);
            if (attempt.Failure is not null)
            {
                failures.Add(attempt.Failure);
                continue;
            }

            if (!AddressesEqual(IPAddress.Parse(controlScenario.ImmediatePeerAddress), attempt.Identity!.ClientAddress))
            {
                failures.Add(new ForwardTrustFailure(
                    scenario.Name,
                    ForwardTrustFailureKind.RequestApplicationNotProven,
                    ForwardTrustDimension.Scenario,
                    "The request sender did not prove that it applied the simulated immediate peer address. No verdict is accepted without a load-bearing peer-seam control."));
            }
        }

        return failures;
    }

    private async Task<ProbeAttempt> SendAsync(
        ForwardTrustScenario scenario,
        ForwardTrustRequestSender requestSender,
        CancellationToken cancellationToken)
    {
        var peerAddress = IPAddress.Parse(scenario.ImmediatePeerAddress);
        var request = new ForwardTrustRequest(scenario, peerAddress);

        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(options.RequestTimeout);
            var observedIdentity = await requestSender(request, timeoutSource.Token)
                .AsTask()
                .WaitAsync(timeoutSource.Token)
                .ConfigureAwait(false);
            if (observedIdentity is null)
            {
                return ProbeAttempt.Failed(CreateHostSetupFailure(scenario.Name, "The test host returned no request identity."));
            }

            return ProbeAttempt.Succeeded(observedIdentity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ProbeAttempt.Failed(CreateProbeFailure(scenario.Name, "The test host or probe exceeded the finite request timeout."));
        }
        catch
        {
            return ProbeAttempt.Failed(CreateHostSetupFailure(scenario.Name, "The test host or probe failed before it returned a request identity."));
        }
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

    private static void AddScenarioSetValidationFailures(
        IReadOnlyList<ForwardTrustScenario?> scenarios,
        IReadOnlyList<List<ForwardTrustFailure>> validationFailures)
    {
        var groups = scenarios
            .Select((scenario, index) => (scenario, index))
            .Where(static item => item.scenario is not null && !string.IsNullOrWhiteSpace(item.scenario.Name))
            .GroupBy(static item => item.scenario!.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var items = group.ToArray();
            if (items.Length < 2)
            {
                continue;
            }

            var equivalent = items.Skip(1).All(item => ScenariosEquivalent(items[0].scenario!, item.scenario!));
            var kind = equivalent ? "Duplicate scenario name" : "Contradictory expectations for scenario name";
            foreach (var item in items)
            {
                validationFailures[item.index].Add(Malformed(item.scenario!, $"{kind} '{group.Key}' is present more than once in the scenario set."));
            }
        }
    }

    private static bool ScenariosEquivalent(ForwardTrustScenario left, ForwardTrustScenario right)
    {
        return string.Equals(left.ImmediatePeerAddress, right.ImmediatePeerAddress, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Path, right.Path, StringComparison.Ordinal)
            && left.HeaderExpectation == right.HeaderExpectation
            && IdentitiesEqual(left.ExpectedIdentity, right.ExpectedIdentity)
            && left.Headers.Count == right.Headers.Count
            && left.Headers.All(header => right.Headers.TryGetValue(header.Key, out var value) && string.Equals(header.Value, value, StringComparison.Ordinal));
    }

    private static bool IdentitiesEqual(ForwardedIdentity left, ForwardedIdentity right)
    {
        return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
            && AddressesEqual(left.ClientAddress, right.ClientAddress);
    }

    private static List<DimensionSpec> GetAssertedDimensions(ForwardTrustScenario scenario)
    {
        var dimensions = new List<DimensionSpec>();
        if (scenario.Headers.Keys.Any(static key => key.Equals("X-Forwarded-Proto", StringComparison.OrdinalIgnoreCase)))
        {
            dimensions.Add(new DimensionSpec(ForwardTrustDimension.Scheme, "X-Forwarded-Proto"));
        }

        if (scenario.ExpectedIdentity.Host is not null
            && scenario.Headers.Keys.Any(static key => key.Equals("X-Forwarded-Host", StringComparison.OrdinalIgnoreCase)))
        {
            dimensions.Add(new DimensionSpec(ForwardTrustDimension.Host, "X-Forwarded-Host"));
        }

        if (scenario.Headers.Keys.Any(static key => key.Equals("X-Forwarded-For", StringComparison.OrdinalIgnoreCase)))
        {
            dimensions.Add(new DimensionSpec(ForwardTrustDimension.ClientAddress, "X-Forwarded-For"));
        }

        return dimensions;
    }

    private static ForwardTrustScenario CreatePeerControlScenario(ForwardTrustScenario scenario, IPAddress controlPeer)
    {
        var headers = scenario.Headers
            .Where(static header => !IsForwardedHeader(header.Key))
            .ToDictionary(static header => header.Key, static header => header.Value, StringComparer.OrdinalIgnoreCase);
        return new ForwardTrustScenario(
            scenario.Name,
            controlPeer.ToString(),
            new ForwardedIdentity("http", controlPeer),
            ForwardTrustHeaderExpectation.Rejected,
            headers,
            scenario.Path);
    }

    private static ForwardTrustScenario CreateCounterfactualScenario(ForwardTrustScenario scenario, string headerName)
    {
        var headers = new Dictionary<string, string>(scenario.Headers, StringComparer.OrdinalIgnoreCase)
        {
            [headerName] = CounterfactualValue(headerName, scenario.Headers[headerName])
        };
        return new ForwardTrustScenario(
            scenario.Name,
            scenario.ImmediatePeerAddress,
            scenario.ExpectedIdentity,
            scenario.HeaderExpectation,
            headers,
            scenario.Path);
    }

    private static string CounterfactualValue(string headerName, string originalValue)
    {
        if (headerName.Equals("X-Forwarded-Proto", StringComparison.OrdinalIgnoreCase))
        {
            return string.Join(", ", SplitHeaderValues(originalValue)
                .Select(static value => value.Equals("https", StringComparison.OrdinalIgnoreCase) ? "http" : "https"));
        }

        if (headerName.Equals("X-Forwarded-Host", StringComparison.OrdinalIgnoreCase))
        {
            return "counterfactual.example";
        }

        return originalValue.Contains("203.0.113.254", StringComparison.Ordinal)
            ? "198.51.100.254"
            : "203.0.113.254";
    }

    private static bool SameDimension(ForwardTrustDimension dimension, ForwardedIdentity left, ForwardedIdentity right)
    {
        return dimension switch
        {
            ForwardTrustDimension.Scheme => string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase),
            ForwardTrustDimension.Host => string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase),
            ForwardTrustDimension.ClientAddress => AddressesEqual(left.ClientAddress, right.ClientAddress),
            _ => true
        };
    }

    private static bool AddressesEqual(IPAddress left, IPAddress right)
    {
        return left.MapToIPv6().Equals(right.MapToIPv6());
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
        var addressMatches = AddressesEqual(expected.ClientAddress, observed.ClientAddress);

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

        foreach (var dimension in GetAssertedDimensions(scenario))
        {
            var matches = SameDimension(dimension.Dimension, observed, expected);
            if (!matches)
            {
                failures.Add(new ForwardTrustFailure(
                    scenario.Name,
                    scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Accepted
                        ? ForwardTrustFailureKind.TrustedHeaderRejected
                        : ForwardTrustFailureKind.UntrustedHeaderAccepted,
                    dimension.Dimension,
                    scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Accepted
                        ? "The trusted forwarded value was not applied for this dimension. Check trusted proxy/network configuration and middleware placement."
                        : "The untrusted forwarded value was applied for this dimension. Check trusted proxy/network configuration and middleware placement; do not broaden trust as a convenience fix."));
            }
        }

        if (GetHopCount(scenario.Headers) > 1 && failures.Any(static failure =>
                failure.Kind is ForwardTrustFailureKind.ClientAddressMismatch
                    or ForwardTrustFailureKind.SchemeMismatch
                    or ForwardTrustFailureKind.HostMismatch
                    or ForwardTrustFailureKind.TrustedHeaderRejected
                    or ForwardTrustFailureKind.UntrustedHeaderAccepted))
        {
            failures.Add(new ForwardTrustFailure(
                scenario.Name,
                ForwardTrustFailureKind.ForwardLimitMismatch,
                ForwardTrustDimension.ForwardLimit,
                "The multi-hop forwarded values did not produce the expected effective identity. Check ForwardLimit and trusted proxy/network configuration."));
        }

        return failures;
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

    private static ForwardTrustFailure Malformed(ForwardTrustScenario scenario, string message)
    {
        return new ForwardTrustFailure(scenario.Name, ForwardTrustFailureKind.MalformedScenario, ForwardTrustDimension.Scenario, message);
    }

    private static ForwardTrustFailure CreateProbeFailure(string scenarioName, string message)
    {
        return new ForwardTrustFailure(scenarioName, ForwardTrustFailureKind.HostProbeFailure, ForwardTrustDimension.Scenario, message);
    }

    private static ForwardTrustFailure CreateHostSetupFailure(string scenarioName, string message)
    {
        return new ForwardTrustFailure(scenarioName, ForwardTrustFailureKind.HostSetupFailure, ForwardTrustDimension.Scenario, message);
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

    private sealed record DimensionSpec(ForwardTrustDimension Dimension, string HeaderName);

    private sealed record ProbeAttempt(ForwardedIdentity? Identity, ForwardTrustFailure? Failure)
    {
        public static ProbeAttempt Succeeded(ForwardedIdentity identity) => new(identity, null);

        public static ProbeAttempt Failed(ForwardTrustFailure failure) => new(null, failure);
    }
}
