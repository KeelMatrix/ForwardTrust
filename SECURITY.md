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

Original address application requires dimension-specific evidence under the `ForwardTrustHeaderExpectation.Accepted` XML contract. A sender can attach the remaining conventional XFF at the same probe point as the identity. Repeatable strict suffix consumption on the unchanged original binds the causal trial to that boundary; residual tokens retain their ports and order, and inconsistent evidence fails closed. An observed absent header is distinct from unavailable capture. Identity-only senders must expose an address effect against a replayed accepted baseline omitting only XFF and retaining other original headers; coincident addresses remain unproven. A scheme or Host commit in a separate stage, or a changed address that stops before an original abort, cannot prove original address application. Host assertions remain optional.

Capture only the actual remaining XFF, never a caller-computed consumed flag or a remainder reconstructed from expectations. X-Original-For can be preseeded and is not proof by itself. Custom header names, inaccessible or inconsistent observations, arbitrary removal/reconstruction of XFF, and later address rewrites cannot silently inherit the conventional-header proof. Self-consistent fabricated observations are outside a behavioral verifier's guarantee; see [compatibility](docs/compatibility.md).
