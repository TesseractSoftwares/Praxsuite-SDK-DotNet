using System;
using System.Collections.Generic;

namespace Praxsuite
{
    /// <summary>
    /// Every failure the gateway reports surfaces as one of these. The <see cref="Code"/>
    /// is stable and safe to branch on; <see cref="Message"/> is human-facing and may change.
    /// </summary>
    public class PraxException : Exception
    {
        /// <summary>Stable machine-readable code, e.g. RATE_LIMIT_EXCEEDED, FORBIDDEN, NETWORK_ERROR.</summary>
        public string Code { get; }

        /// <summary>HTTP status, or 0 for transport-level failures that never reached the gateway.</summary>
        public int StatusCode { get; }

        /// <summary>Per-field validation details, when the gateway supplied them.</summary>
        public IReadOnlyList<string> Details { get; }

        /// <summary>Raw response body, kept for diagnostics. Never contains your API key.</summary>
        public string RawBody { get; }

        public PraxException(string code, string message, int statusCode = 0,
            IReadOnlyList<string> details = null, string rawBody = null)
            : base(message)
        {
            Code = code ?? "UNKNOWN";
            StatusCode = statusCode;
            Details = details ?? Array.Empty<string>();
            RawBody = rawBody;
        }

        // ------------------------------------------------------------ predicates
        // Branch on these rather than string-matching Message.

        /// <summary>The credential is missing, malformed, expired, or the session needs a refresh.</summary>
        public bool IsAuthFailure => StatusCode == 401;

        /// <summary>Authenticated, but this credential or role is not scoped for the operation.</summary>
        public bool IsForbidden => StatusCode == 403;

        /// <summary>Too many calls per minute. Backing off and retrying will succeed.</summary>
        public bool IsRateLimited => Code == "RATE_LIMIT_EXCEEDED";

        /// <summary>
        /// A plan allowance is exhausted (monthly API calls or egress). Retrying will NOT
        /// help - the workspace owner has to upgrade or enable pay-as-you-go.
        /// </summary>
        public bool IsQuotaExceeded => Code == "QUOTA_EXCEEDED" || Code == "EGRESS_LIMIT_EXCEEDED";

        /// <summary>Transport failure: no connectivity, DNS, TLS, or timeout.</summary>
        public bool IsNetworkError => Code == "NETWORK_ERROR" || Code == "TIMEOUT";

        /// <summary>Worth retrying automatically. Quota exhaustion deliberately is not.</summary>
        public bool IsTransient =>
            IsNetworkError || IsRateLimited || (StatusCode >= 500 && StatusCode <= 599);

        public override string ToString()
        {
            var s = "[Praxsuite] " + Code;
            if (StatusCode > 0) s += " (HTTP " + StatusCode + ")";
            s += ": " + Message;
            if (Details.Count > 0) s += "\n  - " + string.Join("\n  - ", Details);
            return s;
        }
    }
}
