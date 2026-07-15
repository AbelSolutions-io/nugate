# Security Policy

## Reporting a vulnerability

Please report vulnerabilities privately — do not open a public issue.

- **Preferred:** GitHub private vulnerability reporting (Security tab → "Report a vulnerability" on this repository)
- **Email:** support@nugate.dev

You can expect an acknowledgement within a few days. Please include reproduction steps and the affected package/version.

## Supported versions

Only the latest released versions of `NuGate.Build` and `NuGate.Tool` receive fixes.

## Scope

NuGate enforces a dependency age policy. It does not detect malware or scan package contents, and reports along those lines ("NuGate didn't catch package X") are expected behavior, not vulnerabilities — see the README's "What this does / does not do." Vulnerabilities in NuGate itself (e.g. in how it parses assets files, talks to the nuget.org API, caches timestamps, or fails open/closed contrary to configuration) are very much in scope.
