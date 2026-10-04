#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.Linq;

namespace Octopus.Tentacle.Sandbox
{
    /// <summary>
    /// Settings for running each script in an Azure Container Apps Sandbox, read from environment variables.
    /// Setting TENTACLE_SCRIPT_RUNNER=AcaSandbox turns the feature on.
    /// </summary>
    public class AcaSandboxConfiguration
    {
        public const string RunnerVariable = "TENTACLE_SCRIPT_RUNNER";

        public const string ContractVariable = "ACA_SANDBOX_CONTRACT";

        public static bool IsEnabled => string.Equals(Environment.GetEnvironmentVariable(RunnerVariable), "AcaSandbox", StringComparison.OrdinalIgnoreCase);

        /// <summary>ACA_SANDBOX_CONTRACT=Kubernetes offers Octopus the Kubernetes agent's script contract instead of ScriptServiceV2.</summary>
        public static bool UsesKubernetesContract => IsEnabled && string.Equals(Environment.GetEnvironmentVariable(ContractVariable), "Kubernetes", StringComparison.OrdinalIgnoreCase);

        /// <summary>Data-plane endpoint, e.g. https://management.southcentralus.azuredevcompute.io</summary>
        public string Endpoint { get; set; } = "";

        /// <summary>/subscriptions/{sub}/resourceGroups/{rg}/sandboxGroups/{group}</summary>
        public string SandboxGroupPath { get; set; } = "";

        public string DiskImageId { get; set; } = "";
        public string Cpu { get; set; } = "2000m";
        public string Memory { get; set; } = "4096Mi";

        /// <summary>Client id of the user-assigned identity used to call the sandbox API; empty uses the system identity.</summary>
        public string? ManagedIdentityClientId { get; set; }

        /// <summary>Coordinator environment variables passed through to each script.</summary>
        public IReadOnlyList<string> PassThroughVariables { get; set; } = Array.Empty<string>();

        /// <summary>Label stamped on every sandbox this coordinator creates.</summary>
        public string CoordinatorName { get; set; } = Environment.MachineName;

        /// <summary>A sandbox with no exec, shell, file or ingress activity for this long is suspended, then deleted.</summary>
        public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>Packages uploaded to the coordinator within this window are copied into each sandbox.</summary>
        public TimeSpan PackageWindow { get; set; } = TimeSpan.FromHours(2);

        /// <summary>Storage account whose containers sandboxes mount read-only (empty: copy everything in).</summary>
        public string? StorageAccount { get; set; }
        public string ToolsContainer { get; set; } = "octopus-tools";
        public string ToolsVolume { get; set; } = "octopus-tools";
        public string FilesContainer { get; set; } = "octopus-files";
        public string FilesVolume { get; set; } = "octopus-files";

        public bool KeepSandboxes { get; set; }

        /// <summary>Start scripts from a snapshot with their tool folders on local disk (ACA_SANDBOX_TOOL_SNAPSHOTS=false turns it off).</summary>
        public bool ToolSnapshots { get; set; } = true;

        /// <summary>Tool snapshots older than this are rebuilt, renewing whatever the image's prepare hook caches (zero: never).</summary>
        public TimeSpan ToolSnapshotMaxAge { get; set; } = TimeSpan.FromHours(24);
        public TimeSpan StartTimeout { get; set; } = TimeSpan.FromMinutes(2);
        public TimeSpan CancelGracePeriod { get; set; } = TimeSpan.FromSeconds(30);

        public static AcaSandboxConfiguration FromEnvironment()
        {
            string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"{name} must be set when {RunnerVariable}=AcaSandbox.");

            string? Optional(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

            return new AcaSandboxConfiguration
            {
                Endpoint = Required("ACA_SANDBOX_ENDPOINT"),
                SandboxGroupPath = Required("ACA_SANDBOX_GROUP_PATH"),
                DiskImageId = Required("ACA_SANDBOX_DISK_ID"),
                Cpu = Optional("ACA_SANDBOX_CPU") ?? "2000m",
                Memory = Optional("ACA_SANDBOX_MEMORY") ?? "4096Mi",
                ManagedIdentityClientId = Optional("ACA_SANDBOX_MI_CLIENT_ID"),
                PassThroughVariables = (Optional("ACA_SANDBOX_ENV_PASSTHROUGH") ?? "")
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => v.Trim())
                    .ToList(),
                CoordinatorName = Optional("ACA_SANDBOX_COORDINATOR_NAME") ?? Environment.MachineName,
                IdleTimeout = TimeSpan.FromSeconds(int.TryParse(Optional("ACA_SANDBOX_IDLE_SECONDS"), out var idleSeconds) ? idleSeconds : 600),
                PackageWindow = TimeSpan.FromMinutes(int.TryParse(Optional("ACA_SANDBOX_PACKAGE_WINDOW_MINUTES"), out var minutes) ? minutes : 120),
                StorageAccount = Optional("ACA_SANDBOX_STORAGE_ACCOUNT"),
                ToolsContainer = Optional("ACA_SANDBOX_TOOLS_CONTAINER") ?? "octopus-tools",
                ToolsVolume = Optional("ACA_SANDBOX_TOOLS_VOLUME") ?? "octopus-tools",
                FilesContainer = Optional("ACA_SANDBOX_FILES_CONTAINER") ?? "octopus-files",
                FilesVolume = Optional("ACA_SANDBOX_FILES_VOLUME") ?? "octopus-files",
                KeepSandboxes = string.Equals(Optional("ACA_SANDBOX_KEEP"), "true", StringComparison.OrdinalIgnoreCase),
                ToolSnapshots = !string.Equals(Optional("ACA_SANDBOX_TOOL_SNAPSHOTS"), "false", StringComparison.OrdinalIgnoreCase),
                ToolSnapshotMaxAge = TimeSpan.FromHours(int.TryParse(Optional("ACA_SANDBOX_TOOL_SNAPSHOT_MAX_AGE_HOURS"), out var maxAgeHours) ? maxAgeHours : 24)
            };
        }
    }
}
#endif
