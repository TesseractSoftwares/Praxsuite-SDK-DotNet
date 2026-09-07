# Praxsuite SDK for .NET

[![NuGet](https://img.shields.io/badge/nuget-Praxsuite.Sdk-blue)](https://www.nuget.org/packages/Praxsuite.Sdk)
[![Targets](https://img.shields.io/badge/targets-netstandard2.1%20%7C%20net8.0-512BD4)](src/Praxsuite.Sdk/Praxsuite.Sdk.csproj)
[![Licence](https://img.shields.io/badge/licence-Praxsuite%20Open%20SDK-blue)](LICENSE)
[![Dependencies](https://img.shields.io/badge/dependencies-none-brightgreen)](src/Praxsuite.Sdk/Praxsuite.Sdk.csproj)

Auth, queries, files and server-authoritative logic for your Praxsuite workspace — from ASP.NET,
Blazor, MAUI, WPF, **Godot 4**, or a console app.

Zero dependencies. One required setting. Refuses to let a secret key reach client code.

---

## Install

```bash
dotnet add package Praxsuite.Sdk
```

Targets `netstandard2.1` and `net8.0`, so it works on modern .NET and on Godot 4's C# runtime
from the same package.

## Use

```csharp
using Praxsuite;

var prax = new PraxsuiteClient("your-workspace-guid");

// Sign a user in.
await prax.Auth.LoginAsync(email, password);

// Read their own row. No user id anywhere: the server's row filter scopes it to them.
var save = await prax.Data.From("Saves").FirstAsync();
Console.WriteLine($"Level {save.GetInt("Level")}");

// Query properly — filters, ordering, paging and aggregates all run in Postgres.
var top = await prax.Data.From("Scores")
    .Select("PlayerName", "Score")
    .Where(PraxFilter.Gte("Score", 1000))
    .OrderByDescending("Score")
    .Limit(10)
    .ToListAsync<Score>();

// Anything a user shouldn't be able to forge goes through the server.
var reward = await prax.Endpoints.CallAsync("claim-daily-reward");
```

That's the whole setup — the publishable key is fetched from the workspace's public config
endpoint on first use, so there's no second value to keep in sync.

> **One thing to get right:** Praxsuite runs several independent tiers and a workspace lives on
> exactly one. Point at the wrong host and every call returns 404 — not an error that explains
> itself. Pass `baseUrl` to match your workspace's API Gateway settings page.

## ASP.NET

```csharp
builder.Services.AddHttpClient();
builder.Services.AddSingleton(sp => new PraxsuiteClient(
    new PraxsuiteOptions
    {
        WorkspaceId = builder.Configuration["Praxsuite:WorkspaceId"],
        PublishableKey = builder.Configuration["Praxsuite:Key"],
    },
    sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
```

Pass your own `HttpClient` in a long-lived service — the SDK only disposes one it created itself,
so a factory-supplied client stays yours.

---

## What's in it

| | |
|---|---|
| `prax.Auth` | Register, sign in, sessions with rotating refresh tokens, password reset, email confirmation, OIDC |
| `prax.Data` | Queries with filters, OR/AND groups, ordering, paging, relations and aggregates; insert, update, delete, upsert |
| `prax.Endpoints` | Call gateway automations — the server-authoritative path |
| `prax.Files` | Upload and download; short-lived signed URLs |
| `prax.Players` | Platform identity links for analytics and account linking |
| `prax.Schema` | Address tables by name instead of GUID |
| `prax.Bus` | The Event Bus - ephemeral realtime between connected clients |

Rows project onto your own types: `.ToListAsync<Score>()`, or read them column-wise with
`row.GetInt("Score")`.

---

## The Event Bus

Ephemeral realtime between connected clients: live cursors, avatars, "user is typing", a
multiplayer lobby. State that is *changing*, where losing a message is fine because a newer one is
100ms behind it.

```csharp
await prax.Auth.LoginAsync(email, password);   // the bus needs a signed-in user, not the key

var room = prax.Bus.Topic("office").Channel("hq");   // the bus "office:hq"

room.On("move", e => MoveAvatar(e.FromUserId, e.Payload));
room.OnPeerLeft(RemoveAvatar);

// JoinAsync returns everyone already there, so a late arrival sees the room rather than
// an empty one until somebody happens to move.
foreach (var peer in await room.JoinAsync())
    MoveAvatar(peer.UserId, peer.Payload);

await room.PublishAsync("move", new { x, y });
```

**A topic must exist before anyone can join it.** Declare it once in the portal under
API Gateway / Event Bus and pick its access rule: open to any signed-in user, gated on a role from
their token, or gated on a grant on that one bus instance. An undeclared topic is refused - which
is what stops somebody else's client squatting in your namespace.

`prax.Bus.Self` is the caller's own bus, `user:self`. The server resolves it to their id, so it can
never address anybody else.

Three things about it are not obvious and will bite:

- **Nothing is persisted.** No history, no retry, no delivery to somebody who was not connected.
  The test is one question: *if this is lost, does it matter?* Yes means it belongs in a table via
  `prax.Data`, or in an automation. No, because a newer one is coming, means it belongs here.
- **Payloads are hostile.** The bus relays opaque JSON between *users* and parses none of it, so
  every server-side sanitizer is bypassed. Treat it the way you would treat a URL query string.
- **You never receive your own event.** Apply your own change locally.

`PublishAsync` does not throw when the bus refuses a frame - a game loop that throws on a rate
limit is worse than one that skips a frame. Read the result when you care:

```csharp
var r = await room.PublishAsync("move", new { x, y });
if (!r.Ok) Log(r.Error);            // e.g. "rate_limited"
if (r.Recipients == 0) { }          // it went out, and nobody was joined
```

`JoinAsync` is the opposite and throws: a publish that does not land is one lost frame, a join that
does not land leaves this client silently absent for the whole session.

Reconnects are handled. The socket comes back with backoff and every channel you still want is
re-joined, because SignalR group membership does not survive a reconnect - a client that only
reconnects is connected, in no groups, and looks for all the world like a broken server.

The SDK speaks SignalR's JSON protocol directly rather than referencing
`Microsoft.AspNetCore.SignalR.Client`. The surface is four message types wide, this package has
zero dependencies (which is what lets it load into Godot and Unity), and that client defaults
`withCredentials` to true - the one setting that makes the handshake fail against our gateway.

---

## Signing in with an external provider

```csharp
var config = await prax.Auth.GetWorkspaceConfigAsync();
foreach (var provider in config.Providers)
    AddButton(provider.Slug, provider.DisplayName);

var start = await prax.Auth.StartOidcLoginAsync("tesseract");
Process.Start(new ProcessStartInfo(start.AuthorizationUrl) { UseShellExecute = true });

// ...once the provider has redirected back with code and state:
await prax.Auth.CompleteOidcLoginAsync(
    "tesseract", code, state,
    "https://app.example/callback");   // byte-identical to the configured redirect URI
```

All four arguments are required, and three of them are why an external sign-in fails when it fails:
the gateway scopes its one-time `state` per provider, consumes it once, and compares the redirect
URI against the value configured for that provider. Pass the URI you were actually redirected to
rather than rebuilding it - that is how it ends up differing by a trailing slash and failing with a
message about redirect URIs that nobody can act on.

The session lands in the same store as a password login, so refresh, sign-out and every
authenticated call behave identically afterwards.

Only the authorization-code flow exists. There is no route that accepts a provider's own
`id_token`, so even a native button has to make the browser hop.

---

## Server or client — it matters

**Server-side** (ASP.NET, a worker, a job): a secret key is appropriate. Read it from
configuration, never a committed file.

**Client-side** (Godot, MAUI, WPF, Blazor WASM): the user can read your binary, so treat it as
untrusted. Ship only a publishable key — the SDK throws on a secret one at construction.

Three rules for a client:

1. **Scope the publishable key to nothing.** It's an identifier, not a credential — extractable
   from any binary, and served unauthenticated by the gateway. Whatever it can reach, the
   anonymous internet can reach. Auth works with zero table scopes.
2. **Give each user their own identity.** Two settings, not one: a `__SELF__` row filter on the
   role's table scope, **and** a `{{claim:sub}}` default value template on the `Enduser` column.
   With only the first, inserts land with a null owner the filter then hides — the user saves and
   can't read it back, with no error anywhere.
3. **Put anything valuable behind an endpoint.**

Full reasoning in **[SECURITY.md](SECURITY.md)**.

---

## Error handling

```csharp
try
{
    await prax.Data.InsertAsync("Scores", values);
}
catch (PraxException ex) when (ex.IsRateLimited)   { /* already retried with backoff */ }
catch (PraxException ex) when (ex.IsQuotaExceeded) { /* plan exhausted — retrying won't help */ }
catch (PraxException ex) when (ex.IsForbidden)     { /* a scope problem, not a query problem */ }
catch (PraxException ex) when (ex.IsNetworkError)  { /* really offline */ }
```

Network errors, timeouts, 5xx and rate limits retry automatically with exponential backoff,
jitter and `Retry-After`. Quota errors deliberately don't.

## Logging

No logging dependency, so no version conflicts. Point the sink at yours:

```csharp
PraxLog.Sink = (level, message) => logger.Log(Map(level), message);
```

Messages reaching the sink are already scrubbed of keys, JWTs and password fields.

---

## Conformance is the law

Praxsuite has SDKs in several languages. Where they touch the gateway they do **not** get to
disagree. A single normative contract defines the shared behaviour, and every SDK implements it
identically:

1. **The contract is normative.** Where this SDK and the contract differ, this SDK is wrong.
2. **Every rule cites the backend source it derives from.** No rule rests on memory.
3. **Every rule exists because getting it wrong fails silently.** Wrong data, not an error.
4. **A behaviour change is a contract change first.** Not an implementation detail.

The contract is internal and deliberately has no public repository. Its value is that it is
authoritative for us, not that it is browsable — and it cites backend internals that are not ours
to publish. Everything a consumer of this SDK needs to know is in this README.

What it pins down, and why each one earned its place:

- **Operators.** Only the thirteen the parser accepts. A friendlier name is a runtime 400.
- **`meta.total`, never `meta.totalCount`.** Reading the wrong name returns nothing and reports
  zero, silently, forever. One SDK shipped that for months.
- **Three response envelopes.** `/query` is bare, `/auth/*` nests under `.data`, `/files` errors
  are a bare string. Assuming one shape mis-parses the other two.
- **`limit` is clamped up to a minimum of 1.** A zero-row count request quietly returns a row.
- **Unscoped updates and deletes refused before sending**, synchronously.
- **Secret keys refused wherever a credential would be exposed.** No flag, no override.
- **No client-supplied identity parameter.** The server ignores it, so it would read as a
  security boundary while being decorative.

The suite runs offline — no workspace, no network, no credentials:

```bash
dotnet test      # 41 checks
```


## Contributing

```bash
dotnet test      # 41 offline tests, no network or workspace needed
```

See [CONTRIBUTING.md](CONTRIBUTING.md). Security issues do **not** go in the issue tracker:
see [SECURITY.md](SECURITY.md).

## License

**Praxsuite Open SDK Licence v1.0** — source-available. See [LICENSE](LICENSE).

- ✅ Use it free in anything you build, **including products you sell**
- ✅ Read, fork, modify and publish your changes
- ❌ Don't resell the SDK itself, or use it to power a competing backend platform

Source-available, not OSI open source — the field-of-use limits fail OSI criteria 5 and 6.
