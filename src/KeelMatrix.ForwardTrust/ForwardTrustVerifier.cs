using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;

namespace KeelMatrix.ForwardTrust;

/// <summary>Executes immutable forwarded-header scenarios through a caller-provided test-host sender.</summary>
public sealed class ForwardTrustVerifier
{
    private const int MaxNameLength = 128;
    private const int MaxPathLength = 2048;
    private const int MaxHeaderLength = ForwardTrustScenario.MaxHeaderValueLength;
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
    /// <remarks>
    /// Host tokens that fail framework normalization cannot establish original-value correspondence,
    /// but do not exclude a different valid effective-hop witness. Without a matching original,
    /// validation returns <see cref="ForwardTrustFailureKind.MalformedScenario"/> before sending.
    /// Original host application must be distinguishable from the accepted path's own baseline;
    /// an indistinguishable baseline returns <see cref="ForwardTrustFailureKind.ForwardedValueNotProven"/>.
    /// Original address application uses repeatable remaining-X-Forwarded-For evidence from the
    /// unchanged original, binding its strict consumed suffix to the effective causal hop index.
    /// Without capture, only an address effect against an accepted baseline omitting X-Forwarded-For
    /// alone can prove application; coincident results remain unproven. A separate scheme, Host or
    /// changed-request commit is insufficient. See <see cref="ForwardTrustHeaderExpectation.Accepted"/>.
    /// Address controls can account for dependent scheme/host traversal by replaying pairs with
    /// only earlier dependent prefixes removed. These must retain the original address, reproduce
    /// the full counterfactual identity, preserve non-target fields within the truncated pair,
    /// and leave the rejected path unchanged. Application trust configuration is never altered.
    /// </remarks>
    /// <param name="scenarios">The finite scenario sequence to validate and execute.</param>
    /// <param name="requestSender">The caller-owned seam that must execute every supplied request against the application pipeline.</param>
    /// <param name="cancellationToken">The cancellation token for the verification run.</param>
    public async Task<ForwardTrustResult> VerifyAsync(
        IEnumerable<ForwardTrustScenario> scenarios,
        ForwardTrustRequestSender requestSender,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(requestSender);

        cancellationToken.ThrowIfCancellationRequested();
        var scenarioList = new List<ForwardTrustScenario?>(Math.Min(options.MaxScenarioCount, 32));
        using var enumerator = scenarios.GetEnumerator();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!enumerator.MoveNext())
            {
                break;
            }

            scenarioList.Add(enumerator.Current);
            if (scenarioList.Count > options.MaxScenarioCount)
            {
                return CreateSingleFailureResult(
                    "verification",
                    ForwardTrustFailureKind.MalformedScenario,
                    ForwardTrustDimension.Scenario,
                    $"The scenario set exceeds the configured maximum of {options.MaxScenarioCount} entries.");
            }
        }

        if (scenarioList.Count == 0)
        {
            return CreateSingleFailureResult(
                "verification",
                ForwardTrustFailureKind.MalformedScenario,
                ForwardTrustDimension.Scenario,
                "The scenario set must contain at least one scenario.");
        }

        var validationFailures = scenarioList
            .Select(ValidateScenario)
            .Select(static failures => failures.ToList())
            .ToArray();
        AddScenarioSetValidationFailures(scenarioList, validationFailures);

        var results = new List<ForwardTrustScenarioResult>(scenarioList.Count);
        for (var index = 0; index < scenarioList.Count; index++)
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
                if (NeedsHostBaselineProbe(scenario))
                {
                    failures.AddRange(await VerifyOriginalHostAsync(
                        scenario, primaryRequest, controlRequest, observedIdentity, controlAttempt.Identity!,
                        requestSender, cancellationToken).ConfigureAwait(false));
                }
                ForwardedHeaderValues.Token? addressBoundary = null;
                if (dimensions.Any(static dimension => dimension.Dimension == ForwardTrustDimension.ClientAddress))
                {
                    var addressProof = await VerifyOriginalAddressAsync(
                        scenario, primaryRequest, controlRequest, observedIdentity, controlAttempt.Identity!,
                        requestSender, cancellationToken).ConfigureAwait(false);
                    failures.AddRange(addressProof.Failures);
                    addressBoundary = addressProof.Boundary;
                }
                foreach (var dimension in dimensions)
                {
                    var lastAttempt = new List<ForwardTrustFailure>();
                    foreach (var token in OriginalValueCandidates(scenario, dimension))
                    {
                        if (dimension.Dimension == ForwardTrustDimension.ClientAddress
                            && addressBoundary is not null && token.Offset != addressBoundary.Offset) { continue; }
                        var attempt = await VerifyDimensionAtHopAsync(
                            scenario, dimension, token, primaryRequest, controlRequest,
                            observedIdentity, controlAttempt.Identity!, requestSender, cancellationToken).ConfigureAwait(false);
                        lastAttempt = attempt.Failures;
                        if (lastAttempt.Count == 0 || !attempt.CanTryAnotherHop) { break; }
                    }
                    failures.AddRange(lastAttempt);
                }
            }
        }

        return new ForwardTrustScenarioResult(scenario.Name, failures.Count == 0, observedIdentity, failures);
    }

    private async Task<AddressProof> VerifyOriginalAddressAsync(
        ForwardTrustScenario scenario, RequestSpec primaryRequest, RequestSpec controlRequest,
        ForwardedIdentity primary, ForwardedIdentity control,
        ForwardTrustRequestSender requestSender, CancellationToken cancellationToken)
    {
        var acceptedRequest = scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Accepted ? primaryRequest : controlRequest;
        var rejectedRequest = scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Rejected ? primaryRequest : controlRequest;
        var accepted = scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Accepted ? primary : control;
        var rejected = scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Rejected ? primary : control;
        var original = scenario.Headers["X-Forwarded-For"];
        var failures = new List<ForwardTrustFailure>();
        var acceptedReplay = await SendAsync(acceptedRequest, requestSender, cancellationToken).ConfigureAwait(false);
        if (acceptedReplay.Failure is not null) { return new([acceptedReplay.Failure], null); }
        var rejectedReplay = await SendAsync(rejectedRequest, requestSender, cancellationToken).ConfigureAwait(false);
        if (rejectedReplay.Failure is not null) { return new([rejectedReplay.Failure], null); }
        AddRepeatabilityFailure(scenario, ForwardTrustDimension.ClientAddress, "accepted original request", accepted, acceptedReplay.Identity!, failures);
        AddRepeatabilityFailure(scenario, ForwardTrustDimension.ClientAddress, "rejected original request", rejected, rejectedReplay.Identity!, failures);
        if (failures.Count > 0) { return new(failures, null); }
        if (rejected.HasForwardedForObservation && !IsUnconsumedOriginal(original, rejected))
        {
            failures.Add(new ForwardTrustFailure(scenario.Name, ForwardTrustFailureKind.UntrustedHeaderAccepted,
                ForwardTrustDimension.ClientAddress, "The rejected original request did not retain the original forwarded-address chain. Check trusted proxy/network configuration and the probe capture point."));
        }
        if (accepted.HasForwardedForObservation)
        {
            var boundary = ForwardedHeaderValues.ConsumedBoundary(original, accepted);
            if (boundary is null)
            {
                failures.Add(OriginalAddressNotProven(scenario));
            }
            return new(failures, boundary);
        }
        var baselineHeaders = new Dictionary<string, string>(acceptedRequest.Headers, StringComparer.OrdinalIgnoreCase);
        baselineHeaders.Remove("X-Forwarded-For");
        var baselineRequest = acceptedRequest with { Headers = baselineHeaders };
        var baseline = await SendAsync(baselineRequest, requestSender, cancellationToken).ConfigureAwait(false);
        if (baseline.Failure is not null) { return new([baseline.Failure], null); }
        var replay = await SendAsync(baselineRequest, requestSender, cancellationToken).ConfigureAwait(false);
        if (replay.Failure is not null) { return new([replay.Failure], null); }
        AddRepeatabilityFailure(scenario, ForwardTrustDimension.ClientAddress, "accepted address-baseline request",
            baseline.Identity!, replay.Identity!, failures);
        if (SameDimension(ForwardTrustDimension.ClientAddress, accepted, baseline.Identity!))
        {
            failures.Add(OriginalAddressNotProven(scenario));
        }
        return new(failures, null);
    }

    private static ForwardTrustFailure OriginalAddressNotProven(ForwardTrustScenario scenario) => new(
        scenario.Name, ForwardTrustFailureKind.ForwardedValueNotProven, ForwardTrustDimension.ClientAddress,
        "Application of the original forwarded address is not proven. Supply repeatable remaining X-Forwarded-For evidence at the identity probe point, or an original address effect against the accepted path's address-only baseline. A scheme, Host or changed-request commit cannot prove original address application.");

    private static bool IsUnconsumedOriginal(string original, ForwardedIdentity observed) =>
        observed.RemainingForwardedFor is not null
        && !observed.RemainingForwardedFor.Any(char.IsControl)
        && ForwardedHeaderValues.Parse(original).Select(static token => token.Value)
            .SequenceEqual(ForwardedHeaderValues.Parse(observed.RemainingForwardedFor).Select(static token => token.Value), StringComparer.Ordinal);

    private async Task<List<ForwardTrustFailure>> VerifyOriginalHostAsync(
        ForwardTrustScenario scenario,
        RequestSpec primaryRequest,
        RequestSpec controlRequest,
        ForwardedIdentity primary,
        ForwardedIdentity control,
        ForwardTrustRequestSender requestSender,
        CancellationToken cancellationToken)
    {
        var acceptedIndex = scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Accepted ? 0 : 1;
        var accepted = acceptedIndex == 0 ? primary : control;
        var acceptedRequest = acceptedIndex == 0 ? primaryRequest : controlRequest;
        var acceptedExpected = acceptedIndex == 0 ? scenario.ExpectedIdentity : scenario.Control!.ExpectedIdentity;
        var alternate = CounterfactualValue("X-Forwarded-Host", acceptedExpected.Host is null ? accepted : acceptedExpected, scenario);
        var baselineHeaders = new Dictionary<string, string>(acceptedRequest.Headers, StringComparer.OrdinalIgnoreCase);
        baselineHeaders.Remove("X-Forwarded-Host");
        var baselineRequest = acceptedRequest with { Headers = baselineHeaders };
        var observations = new List<ForwardedIdentity>(6);
        foreach (var request in new[] { baselineRequest, baselineRequest })
        {
            var attempt = await SendAsync(request, requestSender, cancellationToken).ConfigureAwait(false);
            if (attempt.Failure is not null) { return [attempt.Failure]; }
            observations.Add(attempt.Identity!);
        }
        var failures = new List<ForwardTrustFailure>();
        AddRepeatabilityFailure(scenario, ForwardTrustDimension.Host, "accepted host-baseline request", observations[0], observations[1], failures);
        if (failures.Count > 0 || !SameDimension(ForwardTrustDimension.Host, accepted, observations[0])) { return failures; }

        // A coincident baseline needs a working control on this same path. A
        // different peer cannot show whether application middleware overwrites Host.
        var alternateBaseline = CreateHostBaselineRequest(baselineRequest, alternate);
        var alternateOriginal = CreateHostBaselineRequest(acceptedRequest, alternate);
        foreach (var request in new[] { alternateBaseline, alternateOriginal, alternateBaseline, alternateOriginal })
        {
            var attempt = await SendAsync(request, requestSender, cancellationToken).ConfigureAwait(false);
            if (attempt.Failure is not null) { return [attempt.Failure]; }
            observations.Add(attempt.Identity!);
        }
        AddRepeatabilityFailure(scenario, ForwardTrustDimension.Host, "accepted alternate host-baseline request", observations[2], observations[4], failures);
        AddRepeatabilityFailure(scenario, ForwardTrustDimension.Host, "accepted original with alternate ordinary Host", observations[3], observations[5], failures);
        if (!IdentitiesEqual(accepted, observations[3])
            || !MatchesCounterfactual(ForwardTrustDimension.Host, observations[2], alternate)
            || !NonTargetDimensionsEqual(ForwardTrustDimension.Host, observations[0], observations[2]))
        {
            failures.Add(new ForwardTrustFailure(scenario.Name, ForwardTrustFailureKind.ForwardedValueNotProven,
                ForwardTrustDimension.Host, "Application of the original forwarded host cannot be distinguished from the accepted path's own baseline. Check host policy and middleware placement; an overwritten ordinary Host control cannot prove original acceptance."));
        }
        return failures;
    }

    private async Task<DimensionAttempt> VerifyDimensionAtHopAsync(
        ForwardTrustScenario scenario,
        DimensionSpec dimension,
        ForwardedHeaderValues.Token token,
        RequestSpec primaryRequest,
        RequestSpec controlRequest,
        ForwardedIdentity primary,
        ForwardedIdentity control,
        ForwardTrustRequestSender requestSender,
        CancellationToken cancellationToken)
    {
        var acceptedExpected = scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Accepted
            ? scenario.ExpectedIdentity : scenario.Control!.ExpectedIdentity;
        var counterfactualValue = CounterfactualValue(dimension.HeaderName, acceptedExpected, scenario);
        var primaryCounterfactual = CreateCounterfactualRequest(primaryRequest, dimension.HeaderName, counterfactualValue, token);
        var controlCounterfactual = CreateCounterfactualRequest(controlRequest, dimension.HeaderName, counterfactualValue, token);
        var requests = new[] { primaryCounterfactual, controlCounterfactual, primaryRequest, controlRequest, primaryCounterfactual, controlCounterfactual };
        var observations = new List<ForwardedIdentity>(requests.Length);
        foreach (var request in requests)
        {
            var attempt = await SendAsync(request, requestSender, cancellationToken).ConfigureAwait(false);
            if (attempt.Failure is not null) { return new DimensionAttempt([attempt.Failure], false); }
            observations.Add(attempt.Identity!);
        }
        var failures = new List<ForwardTrustFailure>();
        AddRepeatabilityFailure(scenario, dimension.Dimension, "primary request", primary, observations[2], failures);
        AddRepeatabilityFailure(scenario, dimension.Dimension, "control request", control, observations[3], failures);
        AddRepeatabilityFailure(scenario, dimension.Dimension, "primary counterfactual request", observations[0], observations[4], failures);
        AddRepeatabilityFailure(scenario, dimension.Dimension, "control counterfactual request", observations[1], observations[5], failures);
        if (failures.Count > 0) { return new DimensionAttempt(failures, false); }
        var accepted = scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Accepted ? primary : control;
        var acceptedCounterfactual = scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Accepted ? observations[0] : observations[1];
        var rejected = scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Rejected ? primary : control;
        var rejectedCounterfactual = scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Rejected ? observations[0] : observations[1];
        if (dimension.Dimension == ForwardTrustDimension.ClientAddress
            && MatchesCounterfactual(dimension.Dimension, acceptedCounterfactual, counterfactualValue)
            && !SameDimension(dimension.Dimension, accepted, acceptedCounterfactual)
            && !NonTargetDimensionsEqual(dimension.Dimension, accepted, acceptedCounterfactual)
            && IdentitiesEqual(rejected, rejectedCounterfactual))
        {
            var depth = ForwardedHeaderValues.Parse(primaryRequest.Headers[dimension.HeaderName]).Count(value => value.Offset >= token.Offset);
            var truncatedPrimary = TruncateDependentPrefixes(primaryRequest, depth);
            var truncatedControl = TruncateDependentPrefixes(controlRequest, depth);
            if (!HeadersEqual(primaryRequest.Headers, truncatedPrimary.Headers))
            {
                return await VerifyAddressDependencyAsync(scenario, token, counterfactualValue,
                    truncatedPrimary, truncatedControl, primary, control, observations[0], observations[1],
                    requestSender, cancellationToken).ConfigureAwait(false);
            }
        }
        AddCausalFailures(scenario, dimension.Dimension, counterfactualValue, primary, observations[0], control, observations[1], failures);
        return new DimensionAttempt(failures, IdentitiesEqual(rejected, rejectedCounterfactual));
    }

    private async Task<DimensionAttempt> VerifyAddressDependencyAsync(
        ForwardTrustScenario scenario, ForwardedHeaderValues.Token token, string counterfactualValue,
        RequestSpec primaryRequest, RequestSpec controlRequest,
        ForwardedIdentity primary, ForwardedIdentity control,
        ForwardedIdentity primaryCounterfactual, ForwardedIdentity controlCounterfactual,
        ForwardTrustRequestSender requestSender, CancellationToken cancellationToken)
    {
        var primaryProbe = CreateCounterfactualRequest(primaryRequest, "X-Forwarded-For", counterfactualValue, token);
        var controlProbe = CreateCounterfactualRequest(controlRequest, "X-Forwarded-For", counterfactualValue, token);
        var observations = new List<ForwardedIdentity>(8);
        foreach (var request in new[] { primaryRequest, controlRequest, primaryProbe, controlProbe,
            primaryRequest, controlRequest, primaryProbe, controlProbe })
        {
            var attempt = await SendAsync(request, requestSender, cancellationToken).ConfigureAwait(false);
            if (attempt.Failure is not null) { return new DimensionAttempt([attempt.Failure], false); }
            observations.Add(attempt.Identity!);
        }
        var failures = new List<ForwardTrustFailure>();
        for (var index = 0; index < 4; index++)
        {
            AddRepeatabilityFailure(scenario, ForwardTrustDimension.ClientAddress,
                "address-dependency request", observations[index], observations[index + 4], failures);
        }
        if (failures.Count > 0) { return new DimensionAttempt(failures, false); }
        var rejectedIndex = scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Rejected ? 0 : 1;
        var rejected = rejectedIndex == 0 ? primary : control;
        // Removing only earlier dependent hops must leave the original address
        // intact. Both routes to the stopped counterfactual must give the same
        // full identity; otherwise the drift has not been explained by traversal.
        if (!SameDimension(ForwardTrustDimension.ClientAddress, primary, observations[0])
            || !SameDimension(ForwardTrustDimension.ClientAddress, control, observations[1])
            || !IdentitiesEqual(rejected, observations[rejectedIndex])
            || !IdentitiesEqual(primaryCounterfactual, observations[2])
            || !IdentitiesEqual(controlCounterfactual, observations[3]))
        {
            failures.Add(new ForwardTrustFailure(scenario.Name, ForwardTrustFailureKind.ForwardedValueNotProven,
                ForwardTrustDimension.ClientAddress, "The non-target identity field change was not reproduced by removing only earlier dependent hops. Address-dependent trust traversal is not proven."));
        }
        AddCausalFailures(scenario, ForwardTrustDimension.ClientAddress, counterfactualValue,
            observations[0], observations[2], observations[1], observations[3], failures);
        return new DimensionAttempt(failures,
            IdentitiesEqual(rejected, observations[rejectedIndex])
            && IdentitiesEqual(rejected, observations[rejectedIndex + 2]));
    }

    private static RequestSpec TruncateDependentPrefixes(RequestSpec request, int depth)
    {
        var headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "X-Forwarded-Proto", "X-Forwarded-Host" })
        {
            if (!headers.TryGetValue(name, out var value)) { continue; }
            var tokens = ForwardedHeaderValues.Parse(value);
            if (tokens.Count <= depth) { continue; }
            var first = tokens[tokens.Count - depth];
            var start = first.Offset;
            if (start > 0 && value[start - 1] == '"') { start--; }
            headers[name] = value[start..];
        }
        return request with { Headers = headers };
    }

    private static bool HeadersEqual(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(header => right.TryGetValue(header.Key, out var value) && value == header.Value);

    private static void AddCausalFailures(
        ForwardTrustScenario scenario,
        ForwardTrustDimension dimension,
        string counterfactualValue,
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

        if (SameDimension(dimension, accepted, acceptedCounterfactual)
            || !MatchesCounterfactual(dimension, acceptedCounterfactual, counterfactualValue))
        {
            failures.Add(new ForwardTrustFailure(
                scenario.Name,
                ForwardTrustFailureKind.ForwardedValueNotProven,
                dimension,
                "The accepted-side observation did not reproducibly match the configured forwarded-header counterfactual for this scenario's control pair. The result is not proven to be caused by forwarded-header handling."));
        }

        if (!NonTargetDimensionsEqual(dimension, accepted, acceptedCounterfactual))
        {
            failures.Add(new ForwardTrustFailure(
                scenario.Name,
                ForwardTrustFailureKind.ForwardedValueNotProven,
                dimension,
                "The accepted-side counterfactual changed a non-target identity field. The result is not proven to be caused only by this forwarded-header dimension."));
        }

        if (!SameDimension(dimension, rejected, rejectedCounterfactual))
        {
            failures.Add(new ForwardTrustFailure(
                scenario.Name,
                ForwardTrustFailureKind.UntrustedHeaderAccepted,
                dimension,
                "Changing the forwarded header changed the rejected-side observation. Check trusted proxy/network configuration and middleware placement; do not broaden trust as a convenience fix."));
        }

        if (!NonTargetDimensionsEqual(dimension, rejected, rejectedCounterfactual))
        {
            failures.Add(new ForwardTrustFailure(
                scenario.Name,
                ForwardTrustFailureKind.ForwardedValueNotProven,
                dimension,
                "The rejected-side counterfactual changed a non-target identity field. The request identity is not stable enough to prove this trust boundary."));
        }
    }

    private static void AddRepeatabilityFailure(
        ForwardTrustScenario scenario,
        ForwardTrustDimension dimension,
        string requestKind,
        ForwardedIdentity first,
        ForwardedIdentity replay,
        List<ForwardTrustFailure> failures)
    {
        if (!IdentitiesEqual(first, replay)
            || (dimension == ForwardTrustDimension.ClientAddress
                && (first.HasForwardedForObservation != replay.HasForwardedForObservation
                    || !string.Equals(first.RemainingForwardedFor, replay.RemainingForwardedFor, StringComparison.Ordinal))))
        {
            failures.Add(new ForwardTrustFailure(
                scenario.Name,
                ForwardTrustFailureKind.ForwardedValueNotProven,
                dimension,
                $"Reissuing an identical {requestKind} returned a different identity. The observation is not stable enough to attribute to forwarded-header handling."));
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

        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            timeoutSource.CancelAfter(options.RequestTimeout);
            var pending = requestSender(request, timeoutSource.Token).AsTask();
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= options.RequestTimeout) { timeoutSource.Cancel(); }
            var observedIdentity = await pending.WaitAsync(timeoutSource.Token)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (timeoutSource.IsCancellationRequested || Stopwatch.GetElapsedTime(started) >= options.RequestTimeout)
            {
                return ProbeAttempt.Failed(CreateProbeFailure(requestSpec.Scenario.Name, "The test host or probe exceeded the finite request timeout."));
            }
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
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= options.RequestTimeout)
            {
                return ProbeAttempt.Failed(CreateProbeFailure(requestSpec.Scenario.Name, "The test host or probe exceeded the finite request timeout."));
            }
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
        if (scenario.HeaderLimitsExceeded)
        {
            failures.Add(Malformed(scenario, "The request headers exceed the bounded count or aggregate-size limit."));
        }

        if (scenario.HasDuplicateHeaderNames)
        {
            failures.Add(Malformed(scenario, "Request header names must be unique without regard to case."));
        }

        if (scenario.HasInvalidCounterfactualHost)
        {
            failures.Add(Malformed(scenario, "A caller-supplied counterfactual host must be a bounded ASCII host value without a comma or control characters."));
        }

        if (!Enum.IsDefined(scenario.HeaderExpectation))
        {
            failures.Add(Malformed(scenario, "The forwarded-header expectation must be Accepted or Rejected."));
        }

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
            if (!IsValidHeaderName(header.Key)
                || header.Key.Length > ForwardTrustScenario.MaxHeaderNameLength
                || header.Value is null
                || header.Value.Length > MaxHeaderLength
                || !IsValidHeaderValue(header.Value))
            {
                failures.Add(Malformed(scenario, "A header name or value is invalid or exceeds the bounded request limit."));
                break;
            }
        }

        var dimensions = GetAssertedDimensions(scenario);
        if (dimensions.Count == 0)
        {
            failures.Add(new ForwardTrustFailure(
                scenario.Name,
                ForwardTrustFailureKind.ForwardedValueNotProven,
                ForwardTrustDimension.Headers,
                "The scenario does not assert a supported forwarded header, so trust behavior cannot be proven."));
        }

        var acceptedExpectedIdentity = scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Accepted
            ? scenario.ExpectedIdentity
            : scenario.Control?.ExpectedIdentity;
        if (dimensions.Any(static dimension => dimension.Dimension == ForwardTrustDimension.Host)
            && scenario.Control is not null
            && acceptedExpectedIdentity?.Host is null)
        {
            failures.Add(new ForwardTrustFailure(
                scenario.Name,
                ForwardTrustFailureKind.RequestApplicationNotProven,
                ForwardTrustDimension.Host,
                "The accepted request path must declare the expected host for the original forwarded-host value before a counterfactual can prove host handling."));
        }

        if (acceptedExpectedIdentity is not null)
        {
            foreach (var dimension in dimensions)
            {
                if (dimension.Dimension == ForwardTrustDimension.Host && acceptedExpectedIdentity.Host is null) { continue; }
                if (!OriginalValueCandidates(scenario, dimension).Any())
                {
                    failures.Add(Malformed(scenario,
                        "The accepted-side expectation must match an original forwarded value in every asserted dimension."));
                }
            }
        }

        if (scenario.CounterfactualHost is not null)
        {
            if (!dimensions.Any(static dimension => dimension.Dimension == ForwardTrustDimension.Host))
            {
                failures.Add(Malformed(scenario, "A counterfactual host can only be supplied when the scenario asserts X-Forwarded-Host."));
            }
            else
            {
                if (acceptedExpectedIdentity?.Host is not null
                    && string.Equals(scenario.CounterfactualHost, acceptedExpectedIdentity.Host, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(Malformed(scenario, "A counterfactual host must differ from the accepted-side expected host."));
                }
            }
        }

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

        if (failures.Count == 0)
        {
            ValidateGeneratedCounterfactualRequests(scenario, failures);
        }

        return failures;
    }

    private static void AddScenarioSetValidationFailures(
        IReadOnlyList<ForwardTrustScenario?> scenarios,
        List<ForwardTrustFailure>[] validationFailures)
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

    private static void ValidateGeneratedCounterfactualRequests(
        ForwardTrustScenario scenario,
        List<ForwardTrustFailure> failures)
    {
        var dimensions = GetAssertedDimensions(scenario);
        if (dimensions.Count == 0)
        {
            return;
        }

        var acceptedExpectedIdentity = scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Accepted
            ? scenario.ExpectedIdentity
            : scenario.Control!.ExpectedIdentity;
        var requests = new[]
        {
            CreatePrimaryRequest(scenario),
            CreateControlRequest(scenario)
        };
        foreach (var dimension in dimensions)
        {
            var counterfactualValue = CounterfactualValue(
                dimension.HeaderName,
                acceptedExpectedIdentity,
                scenario);
            foreach (var token in OriginalValueCandidates(scenario, dimension))
                foreach (var request in requests)
                {
                    var generatedRequest = CreateCounterfactualRequest(
                        request,
                        dimension.HeaderName,
                        counterfactualValue,
                        token);
                    _ = ForwardTrustScenario.CopyHeadersBounded(
                        generatedRequest.Headers,
                        out var limitsExceeded,
                        out var duplicateHeaderNames);
                    if (limitsExceeded || duplicateHeaderNames)
                    {
                        failures.Add(Malformed(
                            scenario,
                            "A generated counterfactual request exceeds the bounded header count, value, or aggregate-size limits."));
                        return;
                    }
                }
        }
        if (NeedsHostBaselineProbe(scenario))
        {
            var alternates = new[] { CounterfactualValue("X-Forwarded-Host", acceptedExpectedIdentity, scenario) };
            foreach (var alternate in alternates)
                foreach (var request in requests)
                {
                    var generated = CreateHostBaselineRequest(request, alternate);
                    _ = ForwardTrustScenario.CopyHeadersBounded(generated.Headers, out var exceeded, out var duplicates);
                    if (exceeded || duplicates)
                    {
                        failures.Add(Malformed(scenario, "A generated host-baseline request exceeds the bounded header count, value, or aggregate-size limits."));
                        return;
                    }
                }
        }
    }

    private static bool NeedsHostBaselineProbe(ForwardTrustScenario scenario)
    {
        return GetAssertedDimensions(scenario).Any(static dimension => dimension.Dimension == ForwardTrustDimension.Host);
    }

    private static RequestSpec CreateHostBaselineRequest(RequestSpec request, string alternate)
    {
        var headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase) { ["Host"] = alternate };
        return request with { Headers = headers };
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
        string counterfactualValue,
        ForwardedHeaderValues.Token token)
    {
        var counterfactualHeaderValue = token.Replace(request.Headers[headerName], counterfactualValue);
        var headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase)
        {
            [headerName] = counterfactualHeaderValue
        };
        return request with { Headers = headers };
    }

    private static IEnumerable<ForwardedHeaderValues.Token> OriginalValueCandidates(ForwardTrustScenario scenario, DimensionSpec dimension)
    {
        var acceptedExpected = scenario.HeaderExpectation == ForwardTrustHeaderExpectation.Accepted
            ? scenario.ExpectedIdentity : scenario.Control!.ExpectedIdentity;
        return ForwardedHeaderValues.Parse(scenario.Headers[dimension.HeaderName])
            .Reverse().Where(token => ForwardedHeaderValues.Matches(dimension.Dimension, acceptedExpected, token));
    }

    private static string CounterfactualValue(
        string headerName,
        ForwardedIdentity acceptedExpectedIdentity,
        ForwardTrustScenario scenario)
    {
        if (headerName.Equals("X-Forwarded-Proto", StringComparison.OrdinalIgnoreCase))
        {
            return acceptedExpectedIdentity.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "http" : "https";
        }

        if (headerName.Equals("X-Forwarded-Host", StringComparison.OrdinalIgnoreCase))
        {
            if (scenario.CounterfactualHost is not null)
            {
                return scenario.CounterfactualHost;
            }

            return string.Equals(acceptedExpectedIdentity.Host, "counterfactual.example", StringComparison.OrdinalIgnoreCase)
                ? "alternate-counterfactual.example"
                : "counterfactual.example";
        }

        var first = IPAddress.Parse("203.0.113.254");
        return AddressesEqual(acceptedExpectedIdentity.ClientAddress, first)
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

    private static bool NonTargetDimensionsEqual(ForwardTrustDimension dimension, ForwardedIdentity left, ForwardedIdentity right)
    {
        return dimension switch
        {
            ForwardTrustDimension.Scheme => string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
                && AddressesEqual(left.ClientAddress, right.ClientAddress),
            ForwardTrustDimension.Host => string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
                && AddressesEqual(left.ClientAddress, right.ClientAddress),
            ForwardTrustDimension.ClientAddress => string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase),
            _ => IdentitiesEqual(left, right)
        };
    }

    private static bool MatchesCounterfactual(
        ForwardTrustDimension dimension,
        ForwardedIdentity observed,
        string counterfactualValue)
    {
        return dimension switch
        {
            ForwardTrustDimension.Scheme => string.Equals(observed.Scheme, counterfactualValue, StringComparison.OrdinalIgnoreCase),
            ForwardTrustDimension.Host => string.Equals(observed.Host, counterfactualValue, StringComparison.OrdinalIgnoreCase),
            ForwardTrustDimension.ClientAddress => IPAddress.TryParse(counterfactualValue, out var address)
                && AddressesEqual(observed.ClientAddress, address),
            _ => false
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

        if (GetAssertedDimensions(scenario).Any(dimension =>
                SplitHeaderValues(scenario.Headers[dimension.HeaderName]).Length > 1
                && !SameDimension(dimension.Dimension, observed, request.ExpectedIdentity)))
        {
            failures.Add(new ForwardTrustFailure(
                scenario.Name,
                ForwardTrustFailureKind.ForwardLimitMismatch,
                ForwardTrustDimension.ForwardLimit,
                "A multi-hop asserted forwarded value did not produce the expected identity for its dimension. Check ForwardLimit and trusted proxy/network configuration."));
        }

        return failures;
    }

    private static string[] SplitHeaderValues(string value)
    {
        return ForwardedHeaderValues.Parse(value).Select(static token => token.Value).ToArray();
    }

    private static bool IsValidHeaderName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        foreach (var character in name)
        {
            var isAlphaNumeric = character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9';
            if (!isAlphaNumeric && "!#$%&'*+-.^_`|~".IndexOf(character) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidHeaderValue(string value)
    {
        foreach (var character in value)
        {
            if (character == '\t' || character is >= '\x20' and <= '\x7e' or >= '\x80' and <= '\xff')
            {
                continue;
            }

            return false;
        }

        return true;
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

    private sealed record DimensionAttempt(List<ForwardTrustFailure> Failures, bool CanTryAnotherHop);

    private sealed record AddressProof(List<ForwardTrustFailure> Failures, ForwardedHeaderValues.Token? Boundary);

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
