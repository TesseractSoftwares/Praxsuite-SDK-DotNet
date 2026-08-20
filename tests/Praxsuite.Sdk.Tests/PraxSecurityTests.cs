using NUnit.Framework;

namespace Praxsuite.Tests
{
    /// <summary>
    /// Pins down the guarantees the SDK claims about credentials. If one of these ever fails,
    /// the SDK is unsafe to ship, not merely buggy.
    /// </summary>
    public class PraxSecurityTests
    {
        // Shape-accurate fakes, assembled from fragments on purpose.
        //
        // PraxBuildGuard scans project files for sk_live_ followed by 16+ key characters. A
        // contiguous literal here would match its own guard and fail the build of any project
        // that embeds this package under Assets/ - so the prefix is kept separate from the
        // body, which breaks the pattern in source while producing the same value at runtime.
        private const string FakeSecret = "sk_live_" + "0123456789abcdef0123456789abcdef";
        private const string FakePublishable = "pk_live_" + "fedcba9876543210fedcba9876543210";

        [Test]
        public void Classifies_each_credential_kind()
        {
            Assert.AreEqual(PraxKeyGuard.KeyKind.Secret, PraxKeyGuard.Classify(FakeSecret));
            Assert.AreEqual(PraxKeyGuard.KeyKind.Publishable, PraxKeyGuard.Classify(FakePublishable));
            Assert.AreEqual(PraxKeyGuard.KeyKind.EndUserJwt,
                PraxKeyGuard.Classify("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig"));
            Assert.AreEqual(PraxKeyGuard.KeyKind.Unknown, PraxKeyGuard.Classify(""));
            Assert.AreEqual(PraxKeyGuard.KeyKind.Unknown, PraxKeyGuard.Classify("not a key"));
        }

        [Test]
        public void Client_code_refuses_a_secret_key()
        {
            Assert.Throws<PraxSecurityException>(
                () => PraxKeyGuard.RequireClientSafe(FakeSecret, "a test"));
        }

        [Test]
        public void Client_code_accepts_publishable_keys_and_session_tokens()
        {
            Assert.DoesNotThrow(() => PraxKeyGuard.RequireClientSafe(FakePublishable, "a test"));
            Assert.DoesNotThrow(() => PraxKeyGuard.RequireClientSafe(
                "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig", "a test"));
            Assert.DoesNotThrow(() => PraxKeyGuard.RequireClientSafe(null, "a test"));
        }

        [Test]
        public void Redaction_never_reveals_key_material()
        {
            var redacted = PraxKeyGuard.Redact(FakeSecret);

            StringAssert.StartsWith("sk_live_", redacted);
            Assert.IsFalse(redacted.Contains("0123456789abcdef"));
        }

        [Test]
        public void Log_scrubbing_removes_keys_tokens_and_secret_fields()
        {
            var scrubbed = PraxLog.Scrub(
                "key=" + FakeSecret +
                " jwt=eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhYmMifQ.signaturehere" +
                " body={\"refreshToken\":\"rt-secret-value\",\"password\":\"hunter2\"}");

            Assert.IsFalse(scrubbed.Contains("0123456789abcdef"), "the secret key survived scrubbing");
            Assert.IsFalse(scrubbed.Contains("signaturehere"), "the jwt survived scrubbing");
            Assert.IsFalse(scrubbed.Contains("rt-secret-value"), "the refresh token survived scrubbing");
            Assert.IsFalse(scrubbed.Contains("hunter2"), "the password survived scrubbing");
        }

        [Test]
        public void The_client_refuses_a_secret_key_at_construction()
        {
            // The Unity SDK checks this in its settings asset; here the constructor is the gate.
            Assert.Throws<PraxSecurityException>(() => new PraxsuiteClient(new PraxsuiteOptions
            {
                WorkspaceId = "1eb92f32-d628-4656-8c64-cd0d43c9869d",
                PublishableKey = FakeSecret,
            }));
        }

        [Test]
        public void Plaintext_remote_urls_are_flagged_but_loopback_is_allowed()
        {
            Assert.IsTrue(PraxRoutes.IsInsecureRemote("http://gateway.example.com"));
            Assert.IsFalse(PraxRoutes.IsInsecureRemote("https://gateway.example.com"));
            Assert.IsFalse(PraxRoutes.IsInsecureRemote("http://localhost:5049"));
            Assert.IsFalse(PraxRoutes.IsInsecureRemote("http://127.0.0.1:5049"));
        }
    }
}
