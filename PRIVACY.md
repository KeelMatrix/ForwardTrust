# Privacy

ForwardTrust v1 does not send telemetry, make external network requests, perform DNS or cloud-metadata lookups, or persist scenario data. Verification is local to the caller-provided test host.

The package deliberately omits the shared telemetry dependency. Forwarded-header scenarios can contain IP addresses, host names, authorization headers, cookies, and other security-boundary test data. Keeping v1's verdict path entirely offline is the clearest way to guarantee that those values cannot enter telemetry and that telemetry failure cannot affect a result.

Diagnostics are structured around scenario names, trust dimensions, and bounded expected/observed identity fields. The verifier never copies arbitrary response bodies, cookies, bearer tokens, or unrelated request headers into failures.

The sender can attach one bounded remaining-XFF observation at the identity probe point. It is held in the returned immutable identity for local verification; failures do not echo its raw value. This adds no network, persistence or generic header-capture behavior.
