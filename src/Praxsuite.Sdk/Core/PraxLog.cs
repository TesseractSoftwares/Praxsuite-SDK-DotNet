using System;
using System.Text.RegularExpressions;

namespace Praxsuite
{
    /// <summary>
    /// SDK logging. Every message passes through <see cref="Scrub"/>, so a credential or bearer
    /// token cannot reach a console, a log aggregator, or an error reporter.
    ///
    /// Deliberately not tied to <c>Microsoft.Extensions.Logging</c>: this package has zero
    /// dependencies, and a logging abstraction is the most common source of version conflicts in
    /// a .NET library. Point <see cref="Sink"/> at your own logger instead - one lambda.
    /// </summary>
    public static class PraxLog
    {
        public enum Level { Off = 0, Error = 1, Warning = 2, Info = 3, Verbose = 4 }

        /// <summary>
        /// Defaults to Warning. Raised to Verbose by <c>PraxsuiteOptions.VerboseLogging</c>.
        /// Verbose logs request and response bodies, so keep it off in production.
        /// </summary>
        public static Level Minimum = Level.Warning;

        /// <summary>
        /// Where messages go. Defaults to stderr for warnings and errors, stdout otherwise -
        /// which is the right default for a console app or a container, and wrong for a web
        /// service, so wire your own:
        ///
        /// <code>
        /// PraxLog.Sink = (level, message) => logger.Log(Map(level), message);
        /// </code>
        ///
        /// The message reaching the sink is already scrubbed.
        /// </summary>
        public static Action<Level, string> Sink = DefaultSink;

        private static void DefaultSink(Level level, string message)
        {
            var writer = level <= Level.Warning ? Console.Error : Console.Out;
            writer.WriteLine("[Praxsuite] " + message);
        }

        private static readonly Regex KeyPattern =
            new Regex(@"\b(pk|sk)_live_[A-Za-z0-9]+", RegexOptions.Compiled);

        private static readonly Regex JwtPattern =
            new Regex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]+", RegexOptions.Compiled);

        private static readonly Regex SecretFieldPattern = new Regex(
            "\"(refreshToken|accessToken|password|newPassword|currentPassword|confirmPassword|sessionToken|publicKey)\"\\s*:\\s*\"[^\"]*\"",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Removes credentials from a string. Public because callers building their own
        /// diagnostics should run untrusted text through it too.
        /// </summary>
        public static string Scrub(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            text = KeyPattern.Replace(text, m => m.Groups[1].Value + "_live_<redacted>");
            text = JwtPattern.Replace(text, "<jwt redacted>");
            text = SecretFieldPattern.Replace(text, m =>
            {
                var colon = m.Value.IndexOf(':');
                return m.Value.Substring(0, colon + 1) + "\"<redacted>\"";
            });
            return text;
        }

        private static void Emit(Level level, string message)
        {
            if (Minimum < level) return;
            try
            {
                Sink?.Invoke(level, Scrub(message));
            }
            catch
            {
                // A logging sink must never take down the operation it was describing.
            }
        }

        public static void Error(string message) => Emit(Level.Error, message);

        public static void Error(string message, Exception ex) =>
            Emit(Level.Error, message + "\n" + Scrub(ex?.ToString() ?? ""));

        public static void Warn(string message) => Emit(Level.Warning, message);
        public static void Info(string message) => Emit(Level.Info, message);
        public static void Verbose(string message) => Emit(Level.Verbose, message);
    }
}
