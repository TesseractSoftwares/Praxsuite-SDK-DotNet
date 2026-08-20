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

Rows project onto your own types: `.ToListAsync<Score>()`, or read them column-wise with
`row.GetInt("Score")`.

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
