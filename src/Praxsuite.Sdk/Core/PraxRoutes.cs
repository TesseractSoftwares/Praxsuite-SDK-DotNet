using System;

namespace Praxsuite
{
    /// <summary>
    /// Builds gateway URLs.
    ///
    /// The Praxsuite FrontDoor accepts a short form, <c>/{workspaceId}/query</c>, which it
    /// rewrites to the backend's <c>/api/v1/gateway/{workspaceId}/query</c>. The SDK uses
    /// the short form: it is the documented public shape, and going through the FrontDoor
    /// is what applies the edge rate limit and the response cache.
    ///
    /// Host matters. Praxsuite runs several independent tiers, and a workspace exists on
    /// exactly one of them - a workspace on the Tesseract tier returns 404 on the Cloud
    /// host, not an error you can diagnose from the message. Verified 2026-08-19:
    /// GET {tesseract host}/{workspace}/auth/config returned 200, the Cloud host 404 for
    /// the same workspace. Get the right host from get_gateway_urls, or from the workspace's
    /// API Gateway settings page.
    /// </summary>
    public static class PraxRoutes
    {
        /// <summary>Praxsuite Cloud (multi-tenant).</summary>
        public const string CloudHost = "https://gateway.praxsuite.com";

        /// <summary>The Tesseract Softwares dedicated tier.</summary>
        public const string TesseractHost = "https://gateway.praxsuite.tesseractsoftwares.com";

        /// <summary>Normalises a base URL: trims trailing slashes, defaults to https.</summary>
        public static string NormalizeBaseUrl(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return CloudHost;

            var url = baseUrl.Trim().TrimEnd('/');
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;

            return url;
        }

        /// <summary>True for a plaintext URL that is not a loopback address.</summary>
        public static bool IsInsecureRemote(string baseUrl)
        {
            if (string.IsNullOrEmpty(baseUrl)) return false;
            if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return false;

            var host = baseUrl.Substring("http://".Length);
            var slash = host.IndexOf('/');
            if (slash >= 0) host = host.Substring(0, slash);
            var colon = host.IndexOf(':');
            if (colon >= 0) host = host.Substring(0, colon);

            return !(host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                     host == "127.0.0.1" ||
                     host == "::1" ||
                     host == "0.0.0.0");
        }

        /// <summary>Base for every workspace-scoped route: {host}/{workspaceId}</summary>
        public static string WorkspaceBase(string baseUrl, string workspaceId)
        {
            return NormalizeBaseUrl(baseUrl) + "/" + workspaceId;
        }

        public static string Query(string baseUrl, string workspaceId)
        {
            return WorkspaceBase(baseUrl, workspaceId) + "/query";
        }

        public static string Schema(string baseUrl, string workspaceId)
        {
            return WorkspaceBase(baseUrl, workspaceId) + "/schema";
        }

        public static string Auth(string baseUrl, string workspaceId, string action)
        {
            return WorkspaceBase(baseUrl, workspaceId) + "/auth/" + action;
        }

        public static string Endpoint(string baseUrl, string workspaceId, string slug)
        {
            return WorkspaceBase(baseUrl, workspaceId) + "/endpoint/" + Uri.EscapeDataString(slug);
        }

        public static string Files(string baseUrl, string workspaceId, string suffix = null)
        {
            var url = WorkspaceBase(baseUrl, workspaceId) + "/files";
            return string.IsNullOrEmpty(suffix) ? url : url + "/" + suffix;
        }

        public static string Players(string baseUrl, string workspaceId, string suffix)
        {
            return WorkspaceBase(baseUrl, workspaceId) + "/players/" + suffix;
        }
    }
}
