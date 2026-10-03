#if !NETFRAMEWORK
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Octopus.Tentacle.Sandbox
{
    /// <summary>
    /// Writes write-once, read-many parts of the Tentacle home (tool folders, pushed packages) to Blob containers that
    /// sandboxes mount read-only, so they are not copied into every sandbox.
    /// </summary>
    public class AcaSandboxBlobStore
    {
        readonly AcaSandboxConfiguration config;
        readonly AcaSandboxTokenProvider tokens;
        readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };

        public AcaSandboxBlobStore(AcaSandboxConfiguration config, AcaSandboxTokenProvider tokens)
        {
            this.config = config;
            this.tokens = tokens;
        }

        public bool Enabled => !string.IsNullOrEmpty(config.StorageAccount);

        string BlobUrl(string container, string blobName)
            => $"https://{config.StorageAccount}.blob.core.windows.net/{container}/{string.Join("/", blobName.Split('/').Select(Uri.EscapeDataString))}";

        public async Task<bool> ExistsAsync(string container, string blobName, CancellationToken cancellationToken)
        {
            using var request = await NewRequestAsync(HttpMethod.Head, BlobUrl(container, blobName), cancellationToken);
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return false;
            response.EnsureSuccessStatusCode();
            return true;
        }

        public async Task UploadFileAsync(string container, string blobName, string localPath, CancellationToken cancellationToken)
        {
            await using var content = File.OpenRead(localPath);
            using var request = await NewRequestAsync(HttpMethod.Put, BlobUrl(container, blobName), cancellationToken);
            request.Headers.Add("x-ms-blob-type", "BlockBlob");
            request.Content = new StreamContent(content);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Upload of {blobName} to {container} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        /// <summary>Uploads a folder; <paramref name="lastFileName"/> (a completion marker such as Success.txt) goes last.</summary>
        public async Task UploadDirectoryAsync(string container, string blobPrefix, string localDirectory, string? lastFileName, CancellationToken cancellationToken)
        {
            var files = Directory.EnumerateFiles(localDirectory, "*", SearchOption.AllDirectories).ToList();
            string BlobName(string file) => blobPrefix.TrimEnd('/') + "/" + Path.GetRelativePath(localDirectory, file).Replace('\\', '/');

            var last = files.Where(f => lastFileName != null && Path.GetFileName(f) == lastFileName && Path.GetDirectoryName(f) == localDirectory).ToList();
            await Parallel.ForEachAsync(files.Except(last),
                new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = cancellationToken },
                async (file, ct) => await UploadFileAsync(container, BlobName(file), file, ct));
            foreach (var file in last)
                await UploadFileAsync(container, BlobName(file), file, cancellationToken);
        }

        async Task<HttpRequestMessage> NewRequestAsync(HttpMethod method, string url, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(AcaSandboxTokenProvider.StorageResource, cancellationToken));
            request.Headers.Add("x-ms-version", "2023-11-03");
            return request;
        }
    }
}
#endif
