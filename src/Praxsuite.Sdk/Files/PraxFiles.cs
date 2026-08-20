using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Praxsuite
{
    /// <summary>A stored file.</summary>
    public class PraxFile
    {
        public string Id;
        public string Name;

        /// <summary>Extension including the dot, e.g. ".png".</summary>
        public string Extension;

        public long SizeBytes;
        public DateTimeOffset? CreatedAt;

        public string FullName => Name + Extension;
    }

    /// <summary>
    /// Uploads and downloads workspace files - player avatars, screenshots attached to bug
    /// reports, user-generated level data.
    ///
    /// Content is proxied by the gateway rather than served from storage directly, so a file
    /// URL never carries a credential. When you do need a URL something else can fetch - an
    /// <c>Image</c> component, an external service, a link in an email - ask for a short-lived
    /// signed URL with <see cref="GetSignedUrlAsync"/>.
    /// </summary>
    public class PraxFiles
    {
        private readonly PraxsuiteClient _client;

        internal PraxFiles(PraxsuiteClient client)
        {
            _client = client;
        }

        /// <summary>
        /// Uploads bytes and returns the stored file.
        ///
        /// The gateway enforces an extension allow-list and the workspace plan's size cap, and
        /// rejects anything outside them - so validate size in your UI first rather than making
        /// a player wait through an upload that will be refused.
        /// </summary>
        /// <param name="bytes">File content.</param>
        /// <param name="fileName">Name including extension, e.g. "avatar.png".</param>
        /// <param name="contentType">MIME type. Inferred from the extension when omitted.</param>
        public async Task<PraxFile> UploadAsync(byte[] bytes, string fileName,
            string contentType = null, CancellationToken ct = default)
        {
            if (bytes == null || bytes.Length == 0)
                throw new ArgumentException("There is nothing to upload.", nameof(bytes));
            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("fileName is required, including its extension.",
                    nameof(fileName));

            var body = await PraxHttp.SendMultipartAsync(_client,
                PraxRoutes.Files(_client.BaseUrl, _client.WorkspaceId, "upload"),
                bytes, fileName.Trim(),
                contentType ?? GuessContentType(fileName),
                PraxHttp.AuthMode.PreferSession, ct).ConfigureAwait(false);

            return ParseFile(body);
        }

        /// <summary>
        /// Downloads a file's bytes through the gateway proxy.
        /// </summary>
        public Task<byte[]> DownloadAsync(string fileId, CancellationToken ct = default)
        {
            RequireId(fileId);
            return PraxHttp.GetBytesAsync(_client,
                PraxRoutes.Files(_client.BaseUrl, _client.WorkspaceId, fileId.Trim()),
                PraxHttp.AuthMode.PreferSession, ct);
        }

        /// <summary>
        /// Returns a short-lived signed URL that needs no credential - usable directly in a
        /// UI image loader, a browser, or anything outside your process.
        ///
        /// Treat the URL as a secret for its lifetime: anyone holding it can fetch the file.
        /// Ask for the shortest expiry that works rather than the maximum.
        /// </summary>
        /// <param name="expiresMinutes">Lifetime in minutes. Gateway default 60, maximum 1440.</param>
        public async Task<string> GetSignedUrlAsync(string fileId, int expiresMinutes = 60,
            CancellationToken ct = default)
        {
            RequireId(fileId);

            if (expiresMinutes < 1) expiresMinutes = 1;
            if (expiresMinutes > 1440)
            {
                PraxLog.Warn("Signed URL expiry was capped to the gateway maximum of 1440 minutes.");
                expiresMinutes = 1440;
            }

            var url = PraxRoutes.Files(_client.BaseUrl, _client.WorkspaceId, fileId.Trim() + "/url") +
                      "?expiresMinutes=" + expiresMinutes.ToString(CultureInfo.InvariantCulture);

            var body = await PraxHttp.SendJsonAsync(_client, "GET", url, null,
                PraxHttp.AuthMode.PreferSession, ct).ConfigureAwait(false);

            var signed = PraxHttp.AsString(body, "url") ?? PraxHttp.AsString(body, "sasUrl");
            if (string.IsNullOrEmpty(signed))
                throw new PraxException("NO_SIGNED_URL",
                    "The gateway did not return a signed URL for file " + fileId + ".");

            return signed;
        }

        /// <summary>Lists workspace files, newest first. The gateway returns at most 500.</summary>
        public async Task<IReadOnlyList<PraxFile>> ListAsync(CancellationToken ct = default)
        {
            var body = await PraxHttp.SendJsonAsync(_client, "GET",
                PraxRoutes.Files(_client.BaseUrl, _client.WorkspaceId),
                null, PraxHttp.AuthMode.PreferSession, ct).ConfigureAwait(false);

            var files = new List<PraxFile>();
            if (body.TryGetValue("files", out var node) && node is List<object> list)
            {
                foreach (var item in list)
                    if (item is Dictionary<string, object> map) files.Add(ParseFile(map));
            }
            return files;
        }

        /// <summary>
        /// Deletes a file from storage and drops its record. Irreversible.
        ///
        /// A file still referenced by a table's File column becomes a broken reference, so
        /// clear the reference first if the row is staying.
        /// </summary>
        public async Task DeleteAsync(string fileId, CancellationToken ct = default)
        {
            RequireId(fileId);

            await PraxHttp.SendJsonAsync(_client, "DELETE",
                PraxRoutes.Files(_client.BaseUrl, _client.WorkspaceId, fileId.Trim()),
                null, PraxHttp.AuthMode.PreferSession, ct).ConfigureAwait(false);
        }

        // ---------------------------------------------------------------- helpers

        private static void RequireId(string fileId)
        {
            if (string.IsNullOrWhiteSpace(fileId))
                throw new ArgumentException("fileId is required.", nameof(fileId));
            if (!Guid.TryParse(fileId.Trim(), out _))
                throw new ArgumentException("fileId must be a GUID, got: " + fileId, nameof(fileId));
        }

        private static PraxFile ParseFile(Dictionary<string, object> map)
        {
            var file = new PraxFile
            {
                Id = PraxHttp.AsString(map, "id"),
                Name = PraxHttp.AsString(map, "name"),
                Extension = PraxHttp.AsString(map, "extension")
            };

            if (map.TryGetValue("size", out var size) && size != null)
                long.TryParse(Convert.ToString(size, CultureInfo.InvariantCulture), out file.SizeBytes);

            var created = PraxHttp.AsString(map, "createdDate");
            if (!string.IsNullOrEmpty(created) &&
                DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                file.CreatedAt = parsed;

            return file;
        }

        private static string GuessContentType(string fileName)
        {
            var dot = fileName.LastIndexOf('.');
            var ext = dot >= 0 ? fileName.Substring(dot).ToLowerInvariant() : "";

            switch (ext)
            {
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".svg": return "image/svg+xml";
                case ".json": return "application/json";
                case ".txt": return "text/plain";
                case ".csv": return "text/csv";
                case ".pdf": return "application/pdf";
                case ".zip": return "application/zip";
                case ".mp3": return "audio/mpeg";
                case ".ogg": return "audio/ogg";
                case ".wav": return "audio/wav";
                case ".mp4": return "video/mp4";
                default: return "application/octet-stream";
            }
        }
    }
}
