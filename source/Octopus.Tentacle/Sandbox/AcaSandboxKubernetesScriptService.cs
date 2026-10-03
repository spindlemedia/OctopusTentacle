#if !NETFRAMEWORK
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Octopus.Tentacle.Contracts;
using Octopus.Tentacle.Contracts.KubernetesScriptServiceV1;
using Octopus.Tentacle.Contracts.ScriptServiceV2;
using Octopus.Tentacle.Core.Diagnostics;
using Octopus.Tentacle.Core.Services;
using Octopus.Tentacle.Core.Services.Scripts;
using Octopus.Tentacle.Services.Scripts.Kubernetes;

namespace Octopus.Tentacle.Sandbox
{
    /// <summary>Marks Halibut services registered only when the coordinator offers the Kubernetes agent contract.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class AcaSandboxKubernetesServiceAttribute : Attribute, IServiceAttribute
    {
        public AcaSandboxKubernetesServiceAttribute(Type contractType) => ContractType = contractType;
        public Type ContractType { get; }
    }

    /// <summary>The pod image each running script asked for, read by the sandbox runner.</summary>
    public class AcaSandboxPodImages
    {
        readonly ConcurrentDictionary<ScriptTicket, PodImageConfigurationV1> images = new();

        public void Set(ScriptTicket ticket, PodImageConfigurationV1? image)
        {
            if (image?.Image is { Length: > 0 })
                images[ticket] = image;
        }

        public PodImageConfigurationV1? Get(ScriptTicket ticket) => images.TryGetValue(ticket, out var image) ? image : null;
        public void Remove(ScriptTicket ticket) => images.TryRemove(ticket, out _);
    }

    /// <summary>
    /// The Kubernetes agent's script contract served by the sandbox runner: each script still runs through
    /// ScriptServiceV2's workspace, log and state store, with the pod image choosing the sandbox disk image.
    /// </summary>
    [AcaSandboxKubernetesService(typeof(IKubernetesScriptServiceV1))]
    public class AcaSandboxKubernetesScriptService : IAsyncKubernetesScriptServiceV1
    {
        static readonly Regex LongToken = new(@"[A-Za-z0-9+/=_\-]{24,}", RegexOptions.Compiled);

        readonly ScriptServiceV2 scripts;
        readonly AcaSandboxPodImages podImages;
        readonly ISystemLog log;

        public AcaSandboxKubernetesScriptService(ScriptServiceV2 scripts, AcaSandboxPodImages podImages, ISystemLog log)
        {
            this.scripts = scripts;
            this.podImages = podImages;
            this.log = log;
        }

        public async Task<KubernetesScriptStatusResponseV1> StartScriptAsync(StartKubernetesScriptCommandV1 command, CancellationToken cancellationToken)
        {
            Record(command);
            podImages.Set(command.ScriptTicket, command.PodImageConfiguration);

            var response = await scripts.StartScriptAsync(new StartScriptCommandV2(command.ScriptBody,
                    command.Isolation,
                    command.ScriptIsolationMutexTimeout,
                    command.IsolationMutexName,
                    command.Arguments,
                    command.TaskId,
                    command.ScriptTicket,
                    null,
                    command.Scripts,
                    command.Files.ToArray()),
                cancellationToken);
            return Map(response);
        }

        public async Task<KubernetesScriptStatusResponseV1> GetStatusAsync(KubernetesScriptStatusRequestV1 request, CancellationToken cancellationToken)
            => Map(await scripts.GetStatusAsync(new ScriptStatusRequestV2(request.ScriptTicket, request.LastLogSequence), cancellationToken));

        public async Task<KubernetesScriptStatusResponseV1> CancelScriptAsync(CancelKubernetesScriptCommandV1 command, CancellationToken cancellationToken)
            => Map(await scripts.CancelScriptAsync(new CancelScriptCommandV2(command.ScriptTicket, command.LastLogSequence), cancellationToken));

        public async Task CompleteScriptAsync(CompleteKubernetesScriptCommandV1 command, CancellationToken cancellationToken)
        {
            podImages.Remove(command.ScriptTicket);
            await scripts.CompleteScriptAsync(new CompleteScriptCommandV2(command.ScriptTicket), cancellationToken);
        }

        static KubernetesScriptStatusResponseV1 Map(ScriptStatusResponseV2 response)
            => new(response.Ticket, response.State, response.ExitCode, response.Logs, response.NextLogSequence);

        // Spike instrumentation: what the server sends on this contract, with secrets redacted.
        void Record(StartKubernetesScriptCommandV1 command)
        {
            var record = new
            {
                command.TaskId,
                Ticket = command.ScriptTicket.TaskId,
                command.IsRawScript,
                command.Isolation,
                command.IsolationMutexName,
                command.ScriptIsolationMutexTimeout,
                PodImage = command.PodImageConfiguration == null ? null : new
                {
                    command.PodImageConfiguration.Image,
                    command.PodImageConfiguration.FeedUrl,
                    command.PodImageConfiguration.FeedUsername,
                    FeedPassword = command.PodImageConfiguration.FeedPassword is { Length: > 0 } p ? $"(set, {p.Length} chars)" : null
                },
                command.CalamariImageConfiguration,
                command.ScriptPodServiceAccountName,
                command.ScriptPodPlatform,
                command.AuthContext,
                Arguments = command.Arguments.Select(Redact).ToArray(),
                AdditionalScripts = command.Scripts.ToDictionary(k => k.Key.ToString(), k => RedactBody(k.Value)),
                Files = command.Files.Select(f => f.Name).ToArray(),
                ScriptBody = RedactBody(command.ScriptBody)
            };
            var json = JsonConvert.SerializeObject(record, Formatting.None);
            log.Info($"[k8s-contract] {json}");
        }

        static string RedactBody(string body) => string.Join("\n", body.Split('\n').Select(line => line.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ? "(line with a password redacted)" : Redact(line)));

        static string Redact(string value) => LongToken.Replace(value, m => $"(redacted {m.Length} chars)");
    }
}
#endif
