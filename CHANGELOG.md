# Changelog

All notable changes to the Praxsuite SDK for .NET.
This project follows [Semantic Versioning](https://semver.org/).

## [1.1.0] - 2026-09-07

### Added

- **`prax.Bus` - the Event Bus.** Ephemeral realtime between connected clients.
  `prax.Bus.Topic("office").Channel("hq")` gives a channel with `JoinAsync`, `PublishAsync`,
  `LeaveAsync` and `On`, plus presence and the caller's own `prax.Bus.Self`. Reconnects with
  backoff and re-joins every channel, because SignalR group membership does not survive a
  reconnect and a client that only reconnects is connected, in no groups, and silent.

  It speaks SignalR's JSON protocol over `ClientWebSocket` rather than referencing
  `Microsoft.AspNetCore.SignalR.Client`: the surface is four message types wide, this package
  keeps its zero dependencies (which is what lets it load into Godot and Unity), and that client
  defaults `withCredentials` to true - the one setting that makes the handshake fail against our
  gateway, because the CORS spec forbids answering a credentialed request with the wildcard
  origin the front door sends.

- **`Auth.StartOidcLoginAsync`**, which returns the `state` alongside the URL instead of making
  callers re-parse it, and **`GetWorkspaceConfigAsync().Providers`**, which carries each
  provider's display name so a button can be labelled.

### Fixed

- **`CompleteOidcLoginAsync` could never succeed.** It sent `{ code, state }`, and the gateway
  also requires `providerSlug` - the one-time state is scoped per provider, so omitting it makes
  every callback look expired - and `redirectUri`, which it compares against the value configured
  for that provider. The signature is now
  `CompleteOidcLoginAsync(providerSlug, code, state, redirectUri)`. This is a breaking change to a
  call that returned 401 or 400 every time it was made.

### Changed

- `GetOidcAuthorizationUrlAsync` is obsolete. It still works and returns only the URL; the state
  it discards is required by the callback.
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
