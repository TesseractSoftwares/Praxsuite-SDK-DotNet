using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Praxsuite;

namespace Praxsuite.Tests
{
    /// <summary>
    /// Covers the parts that differ from the Unity SDK: HttpClient transport, retry policy, and
    /// the guardrails that must throw synchronously rather than into a faulted Task.
    /// </summary>
    public class PraxClientTests
    {
        private const string Workspace = "00000000-0000-4000-8000-0000000000ff";
        private const string Table = "2192d04c-4361-4a82-aaec-6e3f2c6172af";
        private const string Key = "pk_live_" + "fedcba9876543210fedcba9876543210";

        /// <summary>Replays scripted responses and records what was sent.</summary>
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();
            public List<HttpRequestMessage> Calls { get; } = new();
            public List<string> Bodies { get; } = new();

            public StubHandler Respond(HttpStatusCode status, string body)
            {
                _responses.Enqueue((status, body));
                return this;
            }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls.Add(request);
                Bodies.Add(request.Content == null
                    ? null
                    : await request.Content.ReadAsStringAsync().ConfigureAwait(false));

                var (status, body) = _responses.Count > 0
                    ? _responses.Dequeue()
                    : (HttpStatusCode.OK, "{\"data\":[],\"meta\":{}}");

                return new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                };
            }
        }

        private static (PraxsuiteClient Client, StubHandler Handler) Build(int maxRetries = 0)
        {
            var handler = new StubHandler();
            var client = new PraxsuiteClient(
                new PraxsuiteOptions
                {
                    WorkspaceId = Workspace,
                    PublishableKey = Key,
                    MaxRetries = maxRetries,
                },
                new HttpClient(handler));
            return (client, handler);
        }

        // ─────────────────────────────────────────────── guardrails throw synchronously

        // This is the defect that shipped in the Unity SDK: validation inside an async method
        // becomes a faulted Task, so a caller who does not await gets no write and no error.
        // Assert.Throws (not ThrowsAsync) is the point - it only passes if the throw reaches
        // the call site.

        [Test]
        public void Update_without_a_filter_throws_at_the_call_site()
        {
            var (client, handler) = Build();
            Assert.Throws<ArgumentException>(() =>
                client.Data.UpdateAsync(Table, new Dictionary<string, object> { { "Level", 1 } }, new PraxFilter[0]));
            Assert.AreEqual(0, handler.Calls.Count, "nothing should reach the network");
        }

        [Test]
        public void Delete_without_a_filter_throws_at_the_call_site()
        {
            var (client, handler) = Build();
            Assert.Throws<ArgumentException>(() => client.Data.DeleteAsync(Table, new PraxFilter[0]));
            Assert.AreEqual(0, handler.Calls.Count);
        }

        [Test]
        public void Insert_with_no_values_throws_at_the_call_site()
        {
            var (client, _) = Build();
            Assert.Throws<ArgumentException>(() =>
                client.Data.InsertAsync(Table, new Dictionary<string, object>()));
        }

        // ──────────────────────────────────────────────────────────────── query shape

        [Test]
        public async Task A_query_sends_the_praxql_shape_the_gateway_expects()
        {
            var (client, handler) = Build();
            handler.Respond(HttpStatusCode.OK,
                "{\"data\":[{\"ID\":\"a\",\"Score\":10}],\"meta\":{\"limit\":5,\"offset\":0,\"count\":1,\"total\":42,\"durationMs\":3}}");

            var page = await client.Data.From(Table)
                .Select("Score")
                .Where(PraxFilter.Gte("Score", 10))
                .OrderByDescending("Score")
                .Limit(5)
                .WithTotalCount()
                .ToPageAsync();

            Assert.AreEqual(42, page.Total);
            Assert.AreEqual(1, page.Rows.Count);

            var sent = PraxJson.ParseObject(handler.Bodies[0]);
            var refs = (Dictionary<string, object>)sent["refs"];
            var query = (Dictionary<string, object>)sent["query"];

            Assert.AreEqual(Table, refs["t"]);
            Assert.AreEqual("t", query["from"]);
            Assert.AreEqual(5L, Convert.ToInt64(query["limit"]));
            Assert.AreEqual(true, sent["includeTotalCount"]);
        }

        [Test]
        public async Task Count_uses_include_total_count_with_a_one_row_fetch()
        {
            // The gateway clamps limit up to a minimum of 1, so a zero-row count is impossible.
            var (client, handler) = Build();
            handler.Respond(HttpStatusCode.OK,
                "{\"data\":[{\"ID\":\"a\"}],\"meta\":{\"limit\":1,\"offset\":0,\"count\":1,\"total\":137}}");

            Assert.AreEqual(137, await client.Data.From(Table).CountAsync());

            var query = (Dictionary<string, object>)PraxJson.ParseObject(handler.Bodies[0])["query"];
            Assert.AreEqual(1L, Convert.ToInt64(query["limit"]));
        }

        // ──────────────────────────────────────────────────────────────── transport

        [Test]
        public async Task The_key_travels_in_a_header_never_in_the_url()
        {
            var (client, handler) = Build();
            await client.Data.From(Table).ToListAsync();

            Assert.IsTrue(handler.Calls[0].Headers.TryGetValues("x-api-key", out var values));
            Assert.AreEqual(Key, values.First());
            StringAssert.DoesNotContain(Key, handler.Calls[0].RequestUri.ToString());
        }

        [Test]
        public void A_403_is_typed_and_not_retried()
        {
            var (client, handler) = Build(maxRetries: 3);
            handler.Respond(HttpStatusCode.Forbidden,
                "{\"error\":{\"code\":\"FORBIDDEN\",\"message\":\"Read access denied.\"}}");

            var ex = Assert.ThrowsAsync<PraxException>(async () =>
                await client.Data.From(Table).ToListAsync());

            Assert.AreEqual("FORBIDDEN", ex.Code);
            Assert.IsTrue(ex.IsForbidden);
            Assert.IsFalse(ex.IsTransient);
            Assert.AreEqual(1, handler.Calls.Count, "a 403 must not be retried");
        }

        [Test]
        public async Task A_rate_limit_is_retried()
        {
            var (client, handler) = Build(maxRetries: 1);
            handler
                .Respond((HttpStatusCode)429, "{\"error\":{\"code\":\"RATE_LIMIT_EXCEEDED\",\"message\":\"slow down\"}}")
                .Respond(HttpStatusCode.OK, "{\"data\":[],\"meta\":{}}");

            await client.Data.From(Table).ToListAsync();
            Assert.AreEqual(2, handler.Calls.Count);
        }

        [Test]
        public void A_quota_error_is_not_retried_despite_sharing_http_429()
        {
            var (client, handler) = Build(maxRetries: 3);
            handler.Respond((HttpStatusCode)429,
                "{\"error\":{\"code\":\"QUOTA_EXCEEDED\",\"message\":\"out of calls\"}}");

            var ex = Assert.ThrowsAsync<PraxException>(async () =>
                await client.Data.From(Table).ToListAsync());

            Assert.IsTrue(ex.IsQuotaExceeded);
            Assert.IsFalse(ex.IsTransient);
            Assert.AreEqual(1, handler.Calls.Count, "retrying an exhausted quota only burns calls");
        }

        // ──────────────────────────────────────────────────────────────────── auth

        private const string LoginOk =
            "{\"isSuccess\":true,\"data\":{\"accessToken\":\"header.payload.sig\",\"refreshToken\":\"rt-1\"," +
            "\"user\":{\"id\":\"u1\",\"email\":\"a@b.c\",\"username\":\"aria\",\"roles\":[\"Player\"]}}}";

        [Test]
        public async Task Login_stores_the_session_and_switches_to_the_session_token()
        {
            var (client, handler) = Build();
            handler.Respond(HttpStatusCode.OK, LoginOk)
                   .Respond(HttpStatusCode.OK, "{\"data\":[],\"meta\":{}}");

            var result = await client.Auth.LoginAsync("a@b.c", "password");

            Assert.IsTrue(result.IsSignedIn);
            Assert.AreEqual("u1", client.Auth.CurrentUserId);
            Assert.AreEqual("aria", client.Auth.CurrentUser.DisplayName);

            await client.Data.From(Table).ToListAsync();
            Assert.AreEqual("Bearer", handler.Calls[1].Headers.Authorization.Scheme);
            Assert.IsFalse(handler.Calls[1].Headers.Contains("x-api-key"));
        }

        [Test]
        public async Task Registration_requiring_confirmation_is_distinguishable_from_a_failure()
        {
            var (client, handler) = Build();
            handler.Respond(HttpStatusCode.OK,
                "{\"isSuccess\":true,\"data\":{\"requiresEmailConfirmation\":true,\"email\":\"a@b.c\"}}");

            var result = await client.Auth.RegisterAsync("a@b.c", "password1");

            Assert.IsFalse(result.IsSignedIn);
            Assert.IsTrue(result.RequiresEmailConfirmation,
                "callers must be able to tell this from a wrong password");
        }

        [Test]
        public async Task Logout_clears_local_state_even_when_the_revoke_fails()
        {
            var (client, handler) = Build();
            handler.Respond(HttpStatusCode.OK, LoginOk)
                   .Respond(HttpStatusCode.InternalServerError, "{\"error\":{\"code\":\"X\",\"message\":\"boom\"}}");

            await client.Auth.LoginAsync("a@b.c", "password");
            await client.Auth.SignOutAsync();

            Assert.IsFalse(client.Auth.IsSignedIn,
                "a failed revoke must not leave the user looking signed in");
        }

        // ────────────────────────────────────────────────────────────── construction

        [Test]
        public void A_plaintext_remote_gateway_is_refused_but_loopback_is_allowed()
        {
            Assert.Throws<PraxSecurityException>(() => new PraxsuiteClient(new PraxsuiteOptions
            {
                WorkspaceId = Workspace, PublishableKey = Key, BaseUrl = "http://gateway.example.com",
            }));

            Assert.DoesNotThrow(() => new PraxsuiteClient(new PraxsuiteOptions
            {
                WorkspaceId = Workspace, PublishableKey = Key, BaseUrl = "http://localhost:5049",
            }));
        }

        [Test]
        public void A_caller_supplied_HttpClient_is_not_disposed_by_the_client()
        {
            // Disposing a client from IHttpClientFactory would break every other consumer of it.
            var http = new HttpClient(new StubHandler());
            var client = new PraxsuiteClient(
                new PraxsuiteOptions { WorkspaceId = Workspace, PublishableKey = Key }, http);

            client.Dispose();

            Assert.DoesNotThrow(() => { var _ = http.Timeout; },
                "the caller still owns the HttpClient it passed in");
        }

        [Test]
        public void The_encrypted_file_store_refuses_an_empty_passphrase()
        {
            // Writing a refresh token to disk in the clear is not a default worth having.
            Assert.Throws<ArgumentException>(() =>
                new PraxEncryptedFileTokenStore(System.IO.Path.GetTempFileName(), ""));
        }

        [Test]
        public void The_encrypted_file_store_round_trips_a_session()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "prax-test-" + Guid.NewGuid().ToString("N") + ".session");
            try
            {
                var store = new PraxEncryptedFileTokenStore(path, "correct horse battery staple");
                store.Save(new PraxSession
                {
                    accessToken = "a.b.c",
                    refreshToken = "rt",
                    endUserId = "u1",
                    email = "a@b.c",
                    roles = new[] { "Player" },
                });

                var reloaded = new PraxEncryptedFileTokenStore(path, "correct horse battery staple").Load();
                Assert.AreEqual("a.b.c", reloaded.accessToken);
                Assert.AreEqual("u1", reloaded.endUserId);
                Assert.AreEqual(new[] { "Player" }, reloaded.roles);

                // A wrong passphrase must fail closed, not throw or return junk.
                Assert.IsNull(new PraxEncryptedFileTokenStore(path, "wrong").Load());
            }
            finally
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
        }
    }
}
