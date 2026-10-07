#if !NETFRAMEWORK
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Octopus.Tentacle.Sandbox
{
    /// <summary>
    /// Asks an image's registry which digest a tag points to now, so a moving tag such as <c>latest</c> maps to the disk
    /// image built from its current contents.
    /// </summary>
    public class AcaSandboxRegistry
    {
        const string ManifestTypes = "application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json, " +
            "application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json";

        static readonly Regex BearerParameter = new(@"(\w+)=""([^""]*)""", RegexOptions.Compiled);

        readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };

        /// <summary>Returns <c>registry/repository@sha256:…</c> for the image as its registry has it now.</summary>
        public async Task<string> PinAsync(string image, string? username, string? password, CancellationToken cancellationToken)
        {
            var reference = ImageReference.Parse(image);
            if (reference.Digest != null)
                return image;

            var url = $"https://{reference.ApiHost}/v2/{reference.Repository}/manifests/{reference.Tag}";
            using var response = await HeadAsync(url, null, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.WwwAuthenticate.Count > 0)
            {
                var token = await GetBearerTokenAsync(response.Headers.WwwAuthenticate.ToString(), username, password, cancellationToken);
                using var retried = await HeadAsync(url, token, cancellationToken);
                return reference.WithDigest(DigestOf(retried, image));
            }

            return reference.WithDigest(DigestOf(response, image));
        }

        /// <summary>The image name without its tag or digest, as <see cref="PinAsync"/> writes it.</summary>
        public static string RepositoryOf(string image)
        {
            var reference = ImageReference.Parse(image);
            return $"{reference.Host}/{reference.Repository}";
        }

        async Task<HttpResponseMessage> HeadAsync(string url, string? bearer, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            request.Headers.Accept.ParseAdd(ManifestTypes);
            if (bearer != null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            return await http.SendAsync(request, cancellationToken);
        }

        async Task<string> GetBearerTokenAsync(string challenge, string? username, string? password, CancellationToken cancellationToken)
        {
            string? realm = null, service = null, scope = null;
            foreach (Match m in BearerParameter.Matches(challenge))
            {
                switch (m.Groups[1].Value)
                {
                    case "realm": realm = m.Groups[2].Value; break;
                    case "service": service = m.Groups[2].Value; break;
                    case "scope": scope = m.Groups[2].Value; break;
                }
            }
            if (realm == null)
                throw new HttpRequestException("Registry challenge has no realm: " + challenge);

            var url = realm + "?service=" + Uri.EscapeDataString(service ?? "") + (scope != null ? "&scope=" + Uri.EscapeDataString(scope) : "");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password)));
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Registry token request failed: {(int)response.StatusCode}");
            var json = JObject.Parse(body);
            return json.Value<string>("token") ?? json.Value<string>("access_token") ?? throw new HttpRequestException("Registry token response had no token");
        }

        static string DigestOf(HttpResponseMessage response, string image)
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Looking up {image} failed: {(int)response.StatusCode}");
            return response.Headers.TryGetValues("Docker-Content-Digest", out var values)
                ? string.Join("", values)
                : throw new HttpRequestException($"The registry returned no digest for {image}");
        }

        internal record ImageReference(string Host, string Repository, string Tag, string? Digest)
        {
            /// <summary>Docker Hub serves its API from a different host than the one image names use.</summary>
            public string ApiHost => Host == "docker.io" ? "registry-1.docker.io" : Host;

            public string WithDigest(string digest) => $"{Host}/{Repository}@{digest}";

            public static ImageReference Parse(string image)
            {
                string? digest = null;
                var at = image.IndexOf('@');
                if (at >= 0)
                {
                    digest = image[(at + 1)..];
                    image = image[..at];
                }

                var slash = image.IndexOf('/');
                var first = slash < 0 ? "" : image[..slash];
                var hasHost = first.Contains('.') || first.Contains(':') || first == "localhost";
                var host = hasHost ? first : "docker.io";
                var path = hasHost ? image[(slash + 1)..] : image;

                var tag = "latest";
                var colon = path.LastIndexOf(':');
                if (colon >= 0)
                {
                    tag = path[(colon + 1)..];
                    path = path[..colon];
                }
                if (host == "docker.io" && !path.Contains('/'))
                    path = "library/" + path;

                return new ImageReference(host, path, tag, digest);
            }
        }
    }
}
#endif
