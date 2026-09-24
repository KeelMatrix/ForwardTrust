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
        var dimensions = GetAssertedDimensions(scenario);
        var primaryRequest = CreatePrimaryRequest(scenario);
        var primaryAttempt = await SendAsync(primaryRequest, requestSender, cancellationToken).ConfigureAwait(false);
        if (primaryAttempt.Failure is not null)
        {
            return new ForwardTrustScenarioResult(scenario.Name, false, null, [primaryAttempt.Failure]);
        }

        var observedIdentity = primaryAttempt.Identity!;
        var failures = CompareIdentity(primaryRequest, observedIdentity);
        if (dimensions.Count > 0)
        {
            var controlRequest = CreateControlRequest(scenario);
            var controlAttempt = await SendAsync(controlRequest, requestSender, cancellationToken).ConfigureAwait(false);
            if (controlAttempt.Failure is not null)
            {
                failures.Add(controlAttempt.Failure);
            }
            else
            {
                failures.AddRange(CompareIdentity(controlRequest, controlAttempt.Identity!));
                foreach (var dimension in dimensions)
                {
                    var acceptedObservation = scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Accepted
                        ? observedIdentity
                        : controlAttempt.Identity!;
                    var counterfactualValue = CounterfactualValue(dimension.HeaderName, acceptedObservation);
                    var primaryCounterfactual = await SendAsync(
                        CreateCounterfactualRequest(primaryRequest, dimension.HeaderName, counterfactualValue),
                        requestSender,
                        cancellationToken).ConfigureAwait(false);
                    var controlCounterfactual = await SendAsync(
                        CreateCounterfactualRequest(controlRequest, dimension.HeaderName, counterfactualValue),
                        requestSender,
                        cancellationToken).ConfigureAwait(false);
                    if (primaryCounterfactual.Failure is not null)
                    {
                        failures.Add(primaryCounterfactual.Failure);
                    }

                    if (controlCounterfactual.Failure is not null)
                    {
                        failures.Add(controlCounterfactual.Failure);
                    }

                    if (primaryCounterfactual.Failure is null && controlCounterfactual.Failure is null)
                    {
                        AddCausalFailures(
                            scenario,
                            dimension.Dimension,
                            observedIdentity,
                            primaryCounterfactual.Identity!,
                            controlAttempt.Identity!,
                            controlCounterfactual.Identity!,
                            failures);
                    }
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

    private static void AddCausalFailures(
        ForwardTrustScenario scenario,
        ForwardTrustDimension dimension,
        ForwardedIdentity primary,
        ForwardedIdentity primaryCounterfactual,
        ForwardedIdentity control,
        ForwardedIdentity controlCounterfactual,
        List<ForwardTrustFailure> failures)
    {
        var accepted = scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Accepted ? primary : control;
        var acceptedCounterfactual = scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Accepted
            ? primaryCounterfactual
            : controlCounterfactual;
        var rejected = scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Rejected ? primary : control;
        var rejectedCounterfactual = scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Rejected
            ? primaryCounterfactual
            : controlCounterfactual;

        if (SameDimension(dimension, accepted, acceptedCounterfactual))
        {
            failures.Add(new ForwardTrustFailure(
                scenario.Name,
                ForwardTrustFailureKind.ForwardedValueNotProven,
                dimension,
                "Changing the forwarded header did not change the accepted-side observation for this scenario's control pair. The result is not proven to be caused by forwarded-header handling."));
        }

        if (!SameDimension(dimension, rejected, rejectedCounterfactual))
        {
            failures.Add(new ForwardTrustFailure(
                scenario.Name,
                ForwardTrustFailureKind.UntrustedHeaderAccepted,
                dimension,
                "Changing the forwarded header changed the rejected-side observation. Check trusted proxy/network configuration and middleware placement; do not broaden trust as a convenience fix."));
        }
    }

    private async Task<ProbeAttempt> SendAsync(
        RequestSpec requestSpec,
        ForwardTrustRequestSender requestSender,
        CancellationToken cancellationToken)
    {
        var request = new ForwardTrustRequest(
            requestSpec.Scenario,
            requestSpec.ImmediatePeerAddress,
            requestSpec.Headers);

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
                return ProbeAttempt.Failed(CreateHostSetupFailure(requestSpec.Scenario.Name, "The test host returned no request identity."));
            }

            return ProbeAttempt.Succeeded(observedIdentity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ProbeAttempt.Failed(CreateProbeFailure(requestSpec.Scenario.Name, "The test host or probe exceeded the finite request timeout."));
        }
        catch
        {
            return ProbeAttempt.Failed(CreateHostSetupFailure(requestSpec.Scenario.Name, "The test host or probe failed before it returned a request identity."));
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

        var dimensions = GetAssertedDimensions(scenario);
        if (dimensions.Count > 0)
        {
            if (scenario.Control is null)
            {
                failures.Add(new ForwardTrustFailure(
                    scenario.Name,
                    ForwardTrustFailureKind.RequestApplicationNotProven,
                    ForwardTrustDimension.Scenario,
                    "The scenario has asserted forwarded dimensions but no opposite-trust control. Each scenario must prove its own accepted and rejected request paths."));
            }
            else if (!IPAddress.TryParse(scenario.Control.ImmediatePeerAddress, out var controlPeer))
            {
                failures.Add(Malformed(scenario, "The opposite-trust control peer address is not a valid IP address."));
            }
            else if (IPAddress.TryParse(scenario.ImmediatePeerAddress, out var primaryPeer)
                && AddressesEqual(primaryPeer, controlPeer))
            {
                failures.Add(new ForwardTrustFailure(
                    scenario.Name,
                    ForwardTrustFailureKind.RequestApplicationNotProven,
                    ForwardTrustDimension.Scenario,
                    "The opposite-trust control must use a different immediate peer from the primary request."));
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
            && ControlsEqual(left.Control, right.Control)
            && left.Headers.Count == right.Headers.Count
            && left.Headers.All(header => right.Headers.TryGetValue(header.Key, out var value) && string.Equals(header.Value, value, StringComparison.Ordinal));
    }

    private static bool ControlsEqual(ForwardTrustControl? left, ForwardTrustControl? right)
    {
        return left is null
            ? right is null
            : right is not null
                && string.Equals(left.ImmediatePeerAddress, right.ImmediatePeerAddress, StringComparison.OrdinalIgnoreCase)
                && IdentitiesEqual(left.ExpectedIdentity, right.ExpectedIdentity);
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

    private static RequestSpec CreatePrimaryRequest(ForwardTrustScenario scenario)
    {
        return new RequestSpec(
            scenario,
            IPAddress.Parse(scenario.ImmediatePeerAddress),
            scenario.ExpectedIdentity,
            scenario.HeaderExpectation,
            scenario.Headers);
    }

    private static RequestSpec CreateControlRequest(ForwardTrustScenario scenario)
    {
        var control = scenario.Control!;
        return new RequestSpec(
            scenario,
            IPAddress.Parse(control.ImmediatePeerAddress),
            control.ExpectedIdentity,
            scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Accepted
                ? ForwardTrustHeaderExpectation.Rejected
                : ForwardTrustHeaderExpectation.Accepted,
            scenario.Headers);
    }

    private static RequestSpec CreateCounterfactualRequest(
        RequestSpec request,
        string headerName,
        string counterfactualValue)
    {
        var headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase)
        {
            [headerName] = counterfactualValue
        };
        return request with { Headers = headers };
    }

    private static string CounterfactualValue(string headerName, ForwardedIdentity acceptedObservation)
    {
        if (headerName.Equals("X-Forwarded-Proto", StringComparison.OrdinalIgnoreCase))
        {
            return acceptedObservation.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "http" : "https";
        }

        if (headerName.Equals("X-Forwarded-Host", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(acceptedObservation.Host, "counterfactual.example", StringComparison.OrdinalIgnoreCase)
                ? "alternate-counterfactual.example"
                : "counterfactual.example";
        }

        var first = IPAddress.Parse("203.0.113.254");
        return AddressesEqual(acceptedObservation.ClientAddress, first)
            ? "198.51.100.254"
            : first.ToString();
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
        RequestSpec request,
        ForwardedIdentity observed)
    {
        var failures = new List<ForwardTrustFailure>();
        var scenario = request.Scenario;
        var expected = request.ExpectedIdentity;
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
                    request.HeaderExpectation == ForwardTrustHeaderExpectation.Accepted
                        ? ForwardTrustFailureKind.TrustedHeaderRejected
                        : ForwardTrustFailureKind.UntrustedHeaderAccepted,
                    dimension.Dimension,
                    request.HeaderExpectation == ForwardTrustHeaderExpectation.Accepted
                        ? "The trusted forwarded value was not applied for this dimension. Check trusted proxy/network configuration and middleware placement."
                        : "The untrusted forwarded value was applied for this dimension. Check trusted proxy/network configuration and middleware placement; do not broaden trust as a convenience fix."));
            }
        }

        if (GetHopCount(request.Headers) > 1 && failures.Any(static failure =>
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

    private sealed record RequestSpec(
        ForwardTrustScenario Scenario,
        IPAddress ImmediatePeerAddress,
        ForwardedIdentity ExpectedIdentity,
        ForwardTrustHeaderExpectation HeaderExpectation,
        IReadOnlyDictionary<string, string> Headers);

    private sealed record ProbeAttempt(ForwardedIdentity? Identity, ForwardTrustFailure? Failure)
    {
        public static ProbeAttempt Succeeded(ForwardedIdentity identity) => new(identity, null);

        public static ProbeAttempt Failed(ForwardTrustFailure failure) => new(null, failure);
    }
}
