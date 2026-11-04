# ADR-021: Validation policy for constrained and experimental features

[日本語](ADR-021-validation-policy_ja.md)

- Date: 2026-11-04
- Status: Accepted
- Related: [ADR-020](ADR-020-app-server-0-166.md)

## Context

The project cannot access enterprise environments such as SSO-enabled organizations or multiple workspaces for real workspace switching. Experimental capabilities can change and have a lower validation priority than stable features.

## Decision

- Do not require enterprise-environment testing, including SSO, when the project has no access to such an environment. Record the environment as unverified.
- Do not test workspace switching while multiple workspaces are unavailable. Record the actual switch as unverified.
- Implement experimental features as needed, with minimal testing. Record which checks were actually performed; do not imply complete coverage.
- Visual Studio Experimental Instance is a host for testing the extension and is distinct from Codex experimental features.
- For the 0.166 work, symbolic-link test cases are excluded from release verification by the maintainer. Record skips as skips.

This policy supersedes ADR-020 and the 0.166 design and plan only where their verification requirements conflict with the decisions above; it does not remove feature or security requirements.

## Alternatives and consequences

Requiring full enterprise, workspace and experimental validation would provide broader coverage but cannot be completed with the available environments and resources. This policy permits delivery while leaving those behaviors less verified. Fake or automated tests do not establish real-environment behavior; document untested scenarios and observed evidence separately.

## Decision history

- 2026-11-04: Adopted the maintainer's validation policy in this conversation. Earlier 0.166 acceptance criteria required broader verification; the change reflects unavailable environments and the decision to limit experimental testing.
