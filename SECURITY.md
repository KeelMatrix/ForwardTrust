# Security Policy

## Reporting a vulnerability

Please do not open a public issue for a suspected vulnerability. Submit a private report through [GitHub's vulnerability reporting form for KeelMatrix/ForwardTrust](https://github.com/KeelMatrix/ForwardTrust/security/advisories/new). Include the affected version, a minimal synthetic reproduction, expected and observed behavior, and impact. Do not include production headers, credentials, cookies, or customer data.

## Supported versions

Only the latest maintained release receives security fixes. ForwardTrust is a test package: under the documented contract that the caller-provided sender executes every request, it verifies application-pipeline behavior and does not secure, configure, or discover production infrastructure. Each request snapshots its immediate-peer address and returns a defensive copy to sender code, so changing an IPv6 scope ID cannot alter later probe inputs. Public result collections are read-only defensive snapshots, keeping aggregate and per-scenario verdicts consistent. A deliberately fabricated, self-consistent sender is outside this black-box verifier's detectable boundary.

## Safe configuration

Trust only explicitly configured proxy addresses and networks. Clearing trusted lists broadens trust and can allow forwarded-header spoofing; it is not a safe workaround.

Accepted expectations must correspond to original forwarded values in every asserted dimension. Controls target a matching effective hop and require exact alternate-value results, stable replays and an unchanged rejected path. A permitted alternate host does not establish acceptance of a different original host. Timeouts include synchronous invocation, but cannot forcibly stop arbitrary sender code; late results fail when control returns.

Host tokens that fail framework normalization, including malformed IDN values, cannot supply an original-value witness. Such a token does not exclude a different valid effective-hop witness. Without a matching original, validation returns structured `MalformedScenario` before sending. Normalization does not establish host-policy acceptance or replace the causal controls.

Original forwarded-host application must be distinguishable from the accepted path's own replayed baseline with the forwarded host omitted. If they coincide, ordinary `Host` controls must move that same path's baseline while the original forwarded host retains its identity. An overwritten or indistinguishable baseline remains unproven; another peer's behavior cannot supply this proof.

When an address counterfactual stops later scheme/host traversal, additional replayed pairs remove only earlier dependent prefixes. They must retain the original addresses, reproduce the full counterfactual identities, preserve non-target fields within the truncated pair, and keep the rejected identity unchanged. Unrelated rewrites and unstable responses remain failures; generated probes never require broader application trust.

Original address application must be distinguishable from the accepted path's replayed baseline with the conventional forwarded identity headers omitted. A coincident address needs an observable original scheme or forwarded-host effect with all original forwarded values retained; the host assertion remains optional. A changed address can avoid a later whole-request abort by stopping trust traversal and committing partially. Such a counterfactual commit never substitutes for original evidence. Indistinguishable original application remains unproven even if the middleware actually consumed the values.
