# Changelog

All notable changes to the Praxsuite SDK for .NET.
This project follows [Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-08-19

First release.

### Added

- Multi-targeted `netstandard2.1` and `net8.0`, so one package serves ASP.NET, Blazor, MAUI,
  WPF, console apps **and Godot 4's C# runtime**.
- **Zero dependencies.** Bundled JSON codec, and logging via a `PraxLog.Sink` lambda rather than
  an `ILogger` reference - both to avoid the version conflicts a library dependency causes.
- **Auth** - register, sign in, sign out, rotating refresh tokens, password reset by emailed
  code, change password, resend confirmation, OIDC. `GetWorkspaceConfigAsync` returns branding
  and enabled features.
- **Data** - fluent queries with filters, OR/AND groups, ordering, paging, total count, relations
  and aggregates; insert, insert-many, update, delete, upsert. Rows project onto your own types.
- **Endpoints** - `CallAsync` for sync automations, `FireAsync` for fire-and-forget telemetry.
- **Files** - upload, download, signed URLs, list, delete.
- **Players** - platform identity links for analytics and account linking.
- `HttpClient` injection for IHttpClientFactory, proxies and custom handlers. The SDK disposes
  only a client it created itself.
- Retry with exponential backoff, jitter and `Retry-After`. Quota exhaustion is deliberately not
  retried.

### Security

- A secret key (`sk_live_`) is refused at construction, with no opt-out flag.
- A plaintext `http://` gateway URL to a remote host throws; loopback is allowed.
- Credentials travel in headers, never in a URL.
- All SDK logging is scrubbed of keys, JWTs and password/token fields.
- Sessions are in memory by default. `PraxEncryptedFileTokenStore` **requires a passphrase you
  supply** - a key derived from machine identifiers only obscures the file, and the class will
  not pretend otherwise by inventing one.
- No client-supplied identity parameter - only a value the server derives itself can scope
  anything.

### Notes

Extracted from the Unity SDK, which was already ~80% plain C#: 15 files ported verbatim, and
only the transport, logging, session store and client host rewritten for .NET. The Unity-only
pieces (the dispatcher, the ScriptableObject settings asset, coroutine helpers) are gone rather
than stubbed.

41 offline tests, mirroring the shared SDK conformance contract. Notably pinned: mutation
guardrails throw **synchronously** rather than returning a faulted Task, `meta.total` is read
rather than `totalCount`, quota and rate limit classify oppositely despite sharing HTTP 429, a
caller-supplied `HttpClient` is not disposed, and the encrypted token store round-trips a
session and fails closed on a wrong passphrase.
