#if !NETFRAMEWORK
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Octopus.Tentacle.Sandbox
{
    /// <summary>
    /// Gets tokens for the sandbox data plane: from the Container Apps managed-identity endpoint when it exists,
    /// otherwise from ACA_SANDBOX_TOKEN (for running the coordinator outside Azure).
    /// </summary>
    public class AcaSandboxTokenProvider
    {
        const string Resource = "https://dynamicsessions.io";

        readonly AcaSandboxConfiguration config;
        readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
        readonly SemaphoreSlim gate = new(1, 1);
        string? token;
        DateTimeOffset expiresOn;

        public AcaSandboxTokenProvider(AcaSandboxConfiguration config)
        {
            this.config = config;
        }

        public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (token != null && DateTimeOffset.UtcNow < expiresOn - TimeSpan.FromMinutes(5))
                    return token;

                var endpoint = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT");
                var header = Environment.GetEnvironmentVariable("IDENTITY_HEADER");
                if (!string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(header))
                {
                    var url = $"{endpoint}?api-version=2019-08-01&resource={Uri.EscapeDataString(Resource)}";
                    if (!string.IsNullOrEmpty(config.ManagedIdentityClientId))
                        url += "&client_id=" + Uri.EscapeDataString(config.ManagedIdentityClientId!);

                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("X-IDENTITY-HEADER", header);
                    using var response = await http.SendAsync(request, cancellationToken);
                    var body = JObject.Parse(await response.Content.ReadAsStringAsync());
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException("Managed identity token request failed: " + body);

                    token = body.Value<string>("access_token");
                    expiresOn = DateTimeOffset.FromUnixTimeSeconds(long.Parse(body.Value<string>("expires_on")!));
                    return token!;
                }

                var staticToken = Environment.GetEnvironmentVariable("ACA_SANDBOX_TOKEN");
                if (!string.IsNullOrEmpty(staticToken))
                {
                    token = staticToken;
                    expiresOn = DateTimeOffset.MaxValue;
                    return token!;
                }

                throw new InvalidOperationException("No sandbox credentials: set IDENTITY_ENDPOINT/IDENTITY_HEADER (managed identity) or ACA_SANDBOX_TOKEN.");
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
#endif
