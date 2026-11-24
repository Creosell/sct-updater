// FakeServer.cs
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Updater.Tests
{
    /// <summary>
    /// In-memory Nextcloud stand-in: serves registered paths and answers 404 for everything else.
    /// </summary>
    internal sealed class FakeServer : HttpMessageHandler
    {
        public const string BaseUrl = "https://nc.test";
        public const string User = "tester";

        private readonly Dictionary<string, byte[]> _files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Paths requested so far, in order.</summary>
        public List<string> Requests { get; } = new List<string>();

        /// <summary>
        /// Configures <see cref="AppConfig"/> for the fake server.
        /// </summary>
        /// <param name="devChannel">True to select the dev channel.</param>
        public static void Configure(bool devChannel)
        {
            AppConfig.ApplyConfiguration(User, "secret", BaseUrl, devChannel);
        }

        /// <summary>
        /// Creates a client wired to this server.
        /// </summary>
        public NextcloudClient CreateClient()
        {
            return new NextcloudClient(new HttpClient(this));
        }

        /// <summary>
        /// Publishes the suite manifest.
        /// </summary>
        /// <param name="json">Raw manifest JSON.</param>
        public void PutSuiteManifest(string json) => Put(AppConfig.SUITE_MANIFEST_URL, Encoding.UTF8.GetBytes(json));

        /// <summary>
        /// Publishes a product file manifest and returns its SHA256 hash.
        /// </summary>
        /// <param name="productId">Product identifier.</param>
        /// <param name="version">Product version.</param>
        /// <param name="json">Raw manifest JSON.</param>
        /// <returns>Hash the client is expected to compute for these bytes.</returns>
        public string PutFileManifest(string productId, string version, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            Put($"{AppConfig.NC_WEBDAV_BASE_URL}/versions/{productId}/{productId}_{version}.json", bytes);
            return Utility.GetBytesHash(bytes);
        }

        /// <summary>
        /// Publishes arbitrary content at a path relative to the Updater root on the server.
        /// </summary>
        /// <param name="relativePath">Path below SCT/Updater, e.g. "runtime/wheels/a.whl".</param>
        /// <param name="content">File content.</param>
        public void PutAt(string relativePath, byte[] content) => Put($"{AppConfig.NC_WEBDAV_BASE_URL}/{relativePath}", content);

        private void Put(string url, byte[] bytes) => _files[url] = bytes;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri.ToString();
            Requests.Add(url);

            var response = _files.TryGetValue(url, out byte[] body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            return Task.FromResult(response);
        }
    }
}
