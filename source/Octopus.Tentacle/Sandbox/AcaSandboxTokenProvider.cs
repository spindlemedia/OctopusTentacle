#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Octopus.Tentacle.Sandbox
{
    /// <summary>
    /// Gets tokens for the sandbox data plane and Blob storage: from the Container Apps managed-identity endpoint when it
    /// exists, otherwise from ACA_SANDBOX_TOKEN / ACA_SANDBOX_STORAGE_TOKEN (for running the coordinator outside Azure).
    /// </summary>
    public class AcaSandboxTokenProvider
    {
        public const string SandboxResource = "https://dynamicsessions.io";
        public const string StorageResource = "https://storage.azure.com";
        public const string ManagementResource = "https://management.azure.com/";

        /// <summary>The user name Azure Container Registry expects with an exchanged refresh token.</summary>
        public const string RegistryUsername = "00000000-0000-0000-0000-000000000000";

        readonly AcaSandboxConfiguration config;
        readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
        readonly SemaphoreSlim gate = new(1, 1);
        readonly Dictionary<string, (string Token, DateTimeOffset ExpiresOn)> cache = new();

        public AcaSandboxTokenProvider(AcaSandboxConfiguration config)
        {
            this.config = config;
        }

        public Task<string> GetTokenAsync(CancellationToken cancellationToken) => GetTokenAsync(SandboxResource, cancellationToken);

        /// <summary>
        /// A short-lived pull credential for an Azure Container Registry, exchanged from this identity's token the way
        /// <c>az acr login --expose-token</c> does. The identity needs AcrPull on the registry.
        /// </summary>
        public async Task<string> GetRegistryTokenAsync(string registry, CancellationToken cancellationToken)
        {
            var accessToken = await GetTokenAsync(ManagementResource, cancellationToken);
            using var response = await http.PostAsync($"https://{registry}/oauth2/exchange", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "access_token",
                ["service"] = registry,
                ["access_token"] = accessToken
            }), cancellationToken);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Exchanging a token for {registry} failed: {(int)response.StatusCode} {body}");
            return JObject.Parse(body).Value<string>("refresh_token")!;
        }

        public async Task<string> GetTokenAsync(string resource, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (cache.TryGetValue(resource, out var cached) && DateTimeOffset.UtcNow < cached.ExpiresOn - TimeSpan.FromMinutes(5))
                    return cached.Token;

                var endpoint = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT");
                var header = Environment.GetEnvironmentVariable("IDENTITY_HEADER");
                if (!string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(header))
                {
                    var url = $"{endpoint}?api-version=2019-08-01&resource={Uri.EscapeDataString(resource)}";
                    if (!string.IsNullOrEmpty(config.ManagedIdentityClientId))
                        url += "&client_id=" + Uri.EscapeDataString(config.ManagedIdentityClientId!);

                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("X-IDENTITY-HEADER", header);
                    using var response = await http.SendAsync(request, cancellationToken);
                    var body = JObject.Parse(await response.Content.ReadAsStringAsync());
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException("Managed identity token request failed: " + body);

                    var token = body.Value<string>("access_token")!;
                    cache[resource] = (token, DateTimeOffset.FromUnixTimeSeconds(long.Parse(body.Value<string>("expires_on")!)));
                    return token;
                }

                var staticToken = Environment.GetEnvironmentVariable(resource == StorageResource ? "ACA_SANDBOX_STORAGE_TOKEN" : "ACA_SANDBOX_TOKEN");
                if (!string.IsNullOrEmpty(staticToken))
                {
                    cache[resource] = (staticToken, DateTimeOffset.MaxValue);
                    return staticToken;
                }

                throw new InvalidOperationException($"No credentials for {resource}: set IDENTITY_ENDPOINT/IDENTITY_HEADER (managed identity) or a static token variable.");
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
#endif
