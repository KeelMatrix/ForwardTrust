# Security Policy

## Reporting a vulnerability

Please do not open a public issue for a suspected vulnerability. Submit a private report through [GitHub's vulnerability reporting form for KeelMatrix/ForwardTrust](https://github.com/KeelMatrix/ForwardTrust/security/advisories/new). Include the affected version, a minimal synthetic reproduction, expected and observed behavior, and impact. Do not include production headers, credentials, cookies, or customer data.

## Supported versions

Only the latest maintained release receives security fixes. ForwardTrust is a test package: under the documented contract that the caller-provided sender executes every request, it verifies application-pipeline behavior and does not secure, configure, or discover production infrastructure. Each request snapshots its immediate-peer address and returns a defensive copy to sender code, so changing an IPv6 scope ID cannot alter later probe inputs. Public result collections are read-only defensive snapshots, keeping aggregate and per-scenario verdicts consistent. A deliberately fabricated, self-consistent sender is outside this black-box verifier's detectable boundary.

## Safe configuration

Trust only explicitly configured proxy addresses and networks. Clearing trusted lists broadens trust and can allow forwarded-header spoofing; it is not a safe workaround.

Accepted expectations must correspond to original forwarded values in every asserted dimension. Controls target a matching effective hop and require exact alternate-value results, stable replays and an unchanged rejected path. A permitted alternate host does not establish acceptance of a different original host. Timeouts include synchronous invocation, but cannot forcibly stop arbitrary sender code; late results fail when control returns.

Host tokens that fail framework normalization, including malformed IDN values, cannot supply an original-value witness. Such a token does not exclude a different valid effective-hop witness. Without a matching original, validation returns structured `MalformedScenario` before sending. Normalization does not establish host-policy acceptance or replace the causal controls.

When both peers expose the same host, ordinary `Host`-header controls distinguish original forwarded-host application from a matching baseline; host-policy rejection cannot pass solely because the ignored original equals that baseline.
