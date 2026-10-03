#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Octopus.Tentacle.Sandbox
{
    /// <summary>
    /// Client for the Azure Container Apps Sandboxes data plane (api-version 2026-02-01-preview).
    /// The exec stream protocol is not documented; it was read from the aca CLI's behaviour.
    /// </summary>
    public class AcaSandboxClient
    {
        const string ApiVersion = "2026-02-01-preview";

        readonly AcaSandboxConfiguration config;
        readonly AcaSandboxTokenProvider tokens;
        readonly HttpClient http;

        public AcaSandboxClient(AcaSandboxConfiguration config, AcaSandboxTokenProvider tokens)
        {
            this.config = config;
            this.tokens = tokens;
            http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        }

        string SandboxesUrl => $"{config.Endpoint.TrimEnd('/')}{config.SandboxGroupPath}/sandboxes";
        string SandboxUrl(string id) => $"{SandboxesUrl}/{id}";

        public async Task<string> CreateAsync(IDictionary<string, string> labels, string? diskImageId, IReadOnlyDictionary<string, string> readOnlyMounts, CancellationToken cancellationToken)
        {
            var body = new JObject
            {
                ["labels"] = JObject.FromObject(labels),
                ["resources"] = new JObject { ["cpu"] = config.Cpu, ["memory"] = config.Memory },
                ["sourcesRef"] = new JObject { ["diskImage"] = new JObject { ["id"] = diskImageId ?? config.DiskImageId } },
                ["lifecycle"] = new JObject
                {
                    ["autoSuspendPolicy"] = new JObject { ["enabled"] = false }
                }
            };
            if (readOnlyMounts.Count > 0)
                body["volumes"] = new JArray(readOnlyMounts.Select(m => new JObject { ["volumeName"] = m.Key, ["mountpoint"] = m.Value, ["readOnly"] = true }));

            var created = await SendJsonAsync(HttpMethod.Put, $"{SandboxesUrl}?api-version={ApiVersion}", body, cancellationToken);
            var id = created.Value<string>("id") ?? throw new InvalidOperationException("Sandbox create returned no id: " + created);

            var deadline = DateTimeOffset.UtcNow + config.StartTimeout;
            var state = created.Value<string>("state");
            while (!string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase))
            {
                if (DateTimeOffset.UtcNow > deadline)
                    throw new TimeoutException($"Sandbox {id} did not reach Running within {config.StartTimeout} (last state {state}).");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                state = (await SendJsonAsync(HttpMethod.Get, $"{SandboxUrl(id)}?api-version={ApiVersion}", null, cancellationToken)).Value<string>("state");
            }

            return id;
        }

        public async Task DeleteAsync(string id, CancellationToken cancellationToken)
        {
            using var request = await NewRequestAsync(HttpMethod.Delete, $"{SandboxUrl(id)}?api-version={ApiVersion}", cancellationToken);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
                throw new HttpRequestException($"Deleting sandbox {id} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        public Task<IReadOnlyList<JObject>> ListAsync(CancellationToken cancellationToken)
            => GetListAsync($"{SandboxesUrl}?api-version={ApiVersion}", cancellationToken);

        // List endpoints have returned both a bare array and {"value": [...]}.
        async Task<IReadOnlyList<JObject>> GetListAsync(string url, CancellationToken cancellationToken)
        {
            using var request = await NewRequestAsync(HttpMethod.Get, url, cancellationToken);
            using var response = await http.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"GET {url} failed: {(int)response.StatusCode} {text}");

            var token = JToken.Parse(text);
            var items = token as JArray ?? (JArray?)token["value"] ?? new JArray();
            var result = new List<JObject>();
            foreach (var item in items)
                result.Add((JObject)item);
            return result;
        }

        /// <summary>
        /// Returns the id of a Ready disk image built from <paramref name="image"/>, building one when none exists.
        /// Building one from a 2 GB image took about 30 s in the spike.
        /// </summary>
        public async Task<string> EnsureDiskImageAsync(string image, string? username, string? token, Action<string> progress, CancellationToken cancellationToken)
        {
            var diskImagesUrl = $"{config.Endpoint.TrimEnd('/')}{config.SandboxGroupPath}/diskimages?api-version={ApiVersion}";
            var name = "octo-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(image))).Substring(0, 16).ToLowerInvariant();
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(20);
            var requested = false;

            while (true)
            {
                JObject? match = null;
                foreach (var item in await GetListAsync(diskImagesUrl, cancellationToken))
                {
                    if (item["image"]?.Value<string>("base") == image && item["status"]?.Value<string>("state") != "Failed")
                        match = item;
                }

                var state = match?["status"]?.Value<string>("state");
                if (state == "Ready")
                    return match!.Value<string>("id")!;

                if (match == null)
                {
                    if (requested)
                        throw new InvalidOperationException($"Disk image for {image} failed to build or disappeared.");

                    var body = new JObject
                    {
                        ["image"] = new JObject { ["base"] = image },
                        ["labels"] = new JObject { ["name"] = name, ["octopus-image"] = "true" }
                    };
                    if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(token))
                        body["registryCredentials"] = new JObject { ["username"] = username, ["token"] = token };

                    progress($"Building sandbox disk image from {image}");
                    await SendJsonAsync(HttpMethod.Put, diskImagesUrl, body, cancellationToken);
                    requested = true;
                }
                else
                {
                    progress($"Waiting for sandbox disk image from {image} ({state})");
                }

                if (DateTimeOffset.UtcNow > deadline)
                    throw new TimeoutException($"Disk image for {image} was not Ready within 20 minutes.");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }

        public async Task UploadFileAsync(string id, string remotePath, Stream content, CancellationToken cancellationToken)
        {
            using var request = await NewRequestAsync(HttpMethod.Put, $"{SandboxUrl(id)}/files?api-version={ApiVersion}&path={Uri.EscapeDataString(remotePath)}&createDirs=true", cancellationToken);
            request.Content = new StreamContent(content);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Upload of {remotePath} to sandbox {id} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        public async Task DownloadFileAsync(string id, string remotePath, Stream destination, CancellationToken cancellationToken)
        {
            using var request = await NewRequestAsync(HttpMethod.Get, $"{SandboxUrl(id)}/files?api-version={ApiVersion}&path={Uri.EscapeDataString(remotePath)}", cancellationToken);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Download of {remotePath} from sandbox {id} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            await response.Content.CopyToAsync(destination, cancellationToken);
        }

        /// <summary>
        /// Runs a command over the exec WebSocket, streaming output as it arrives. Dropping the socket kills the process.
        /// </summary>
        public async Task<int> ExecAsync(
            string id,
            string command,
            IReadOnlyList<string> args,
            IReadOnlyDictionary<string, string> environment,
            Func<bool, byte[], Task> onOutput,
            CancellationToken cancellationToken)
        {
            var url = SandboxUrl(id).Replace("https://", "wss://") + $"/exec/stream?api-version={ApiVersion}";
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Authorization", "Bearer " + await tokens.GetTokenAsync(cancellationToken));
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            await ws.ConnectAsync(new Uri(url), cancellationToken);

            var start = new JObject
            {
                ["type"] = "start",
                ["start"] = new JObject
                {
                    ["command"] = command,
                    ["args"] = new JArray(args),
                    ["environment"] = JObject.FromObject(environment),
                    ["tty"] = false
                }
            };
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(start.ToString(Formatting.None))), WebSocketMessageType.Text, true, cancellationToken);

            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            while (true)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new IOException($"Sandbox exec stream closed before an exit code arrived: {result.CloseStatus} {result.CloseStatusDescription}");

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                var frame = JObject.Parse(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                message.SetLength(0);

                switch (frame.Value<string>("type"))
                {
                    case "stdout":
                        await onOutput(false, Convert.FromBase64String(frame.Value<string>("data") ?? ""));
                        break;
                    case "stderr":
                        await onOutput(true, Convert.FromBase64String(frame.Value<string>("data") ?? ""));
                        break;
                    case "exit_code":
                        var exitCode = frame.Value<int?>("exitCode") ?? -1;
                        try
                        {
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                        }
                        catch
                        {
                            // The server usually closes first.
                        }
                        return exitCode;
                    case "error":
                        throw new IOException("Sandbox exec failed: " + frame.Value<string>("data"));
                }
            }
        }

        /// <summary>Runs a short command and returns its exit code, stdout and stderr.</summary>
        public async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string id, string bashScript, CancellationToken cancellationToken)
        {
            var stdout = new MemoryStream();
            var stderr = new MemoryStream();
            var exitCode = await ExecAsync(id,
                "/bin/bash",
                new[] { "-c", bashScript },
                new Dictionary<string, string>(),
                (isError, bytes) =>
                {
                    (isError ? stderr : stdout).Write(bytes, 0, bytes.Length);
                    return Task.CompletedTask;
                },
                cancellationToken);
            return (exitCode, Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()));
        }

        async Task<JObject> SendJsonAsync(HttpMethod method, string url, JObject? body, CancellationToken cancellationToken)
        {
            using var request = await NewRequestAsync(method, url, cancellationToken);
            if (body != null)
                request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{method} {url} failed: {(int)response.StatusCode} {text}");
            return string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
        }

        async Task<HttpRequestMessage> NewRequestAsync(HttpMethod method, string url, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(cancellationToken));
            return request;
        }
    }
}
#endif
