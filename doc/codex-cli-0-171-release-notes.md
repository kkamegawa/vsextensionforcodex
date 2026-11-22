# Codex CLI 0.171 release notes

[日本語](codex-cli-0-171-release-notes_ja.md)

## Enterprise MCP sign-in

Use **Codex CLI 0.171.0 or later** when your enterprise identity provider issues an OpenID Connect ID token without a refresh token. Open MCP details in the Codex tool window and choose **Sign in to MCP server** for the intended registration. The CLI performs the token exchange and validates the ID token. If the token expires or becomes invalid, sign in to that registration again.

The 0.169 and 0.170 releases require a refresh token for this sign-in flow. For a remote connection, the version of the CLI running the remote app-server determines support.

## OTLP HTTP header helpers

| CLI | Configuration |
| --- | --- |
| 0.169 | Use the existing exporter configuration. Header helpers require 0.170 or later. |
| 0.170 | Set `otel.headers_helper` and optionally `otel.headers_helper_refresh_interval_ms` in user configuration for an explicitly configured user OTLP HTTP exporter. |
| 0.171 | System `requirements.toml` and MDM requirements can also provide an absolute helper executable path and an OTLP HTTP exporter with a nonempty endpoint in the same policy layer. Managed helper support is limited to those two policy sources. |

The CLI validates and runs the helper and applies refreshed headers to the eligible exporter. Manage the executable and its configuration on the machine running the CLI. The extension displays MCP sign-in status and keeps credentials and telemetry header values within the CLI.

**Implemented, unverified in an Enterprise environment.** Enterprise environment acceptance is waived. Ordinary build and regression checks apply.

[Enterprise MCP sign-in](https://github.com/openai/codex/pull/62129) · [User OTLP header helpers](https://github.com/openai/codex/pull/61028) · [Managed OTLP header helpers](https://github.com/openai/codex/pull/62110)
