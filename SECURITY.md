# Security Policy

## Reporting a vulnerability

Please do not open a public issue for a suspected vulnerability. Use GitHub's private vulnerability reporting for the [KeelMatrix/ForwardTrust repository](https://github.com/KeelMatrix/ForwardTrust/security/advisories/new) when available. Include the affected version, a minimal synthetic reproduction, expected and observed behavior, and impact. Do not include production headers, credentials, cookies, or customer data.

## Supported versions

Only the latest maintained release receives security fixes. ForwardTrust is a test package: under the documented contract that the caller-provided sender executes every request, it verifies application-pipeline behavior and does not secure, configure, or discover production infrastructure. A deliberately fabricated, self-consistent sender is outside this black-box verifier's detectable boundary.

## Safe configuration

Trust only explicitly configured proxy addresses and networks. Clearing trusted lists broadens trust and can allow forwarded-header spoofing; it is not a safe workaround.
