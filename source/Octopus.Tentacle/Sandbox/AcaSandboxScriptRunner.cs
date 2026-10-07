#if !NETFRAMEWORK
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Octopus.Tentacle.Core.Configuration;
using Octopus.Tentacle.Contracts;
using Octopus.Tentacle.Core.Diagnostics;
using Octopus.Tentacle.Core.Services.Scripts;
using Octopus.Tentacle.Core.Services.Scripts.Logging;
using Octopus.Tentacle.Scripts;

namespace Octopus.Tentacle.Sandbox
{
    /// <summary>
    /// Runs each script in its own short-lived Azure Container Apps Sandbox built from a disk image that already holds
    /// the tools (and usually Calamari). The coordinator keeps the Tentacle home on its own disk: before the script it
    /// copies in the work folder and the home files the script refers to, at the same absolute paths; afterwards it
    /// copies back every file under the home the script created or changed. Artifacts are copied back as soon as the
    /// script announces them, before the announcement reaches the log, because the server may fetch them straight away.
    /// </summary>
    public class AcaSandboxScriptRunner : IScriptRunner
    {
        const string StageInPath = "/tmp/octopus-stage-in.tgz";
        const string StageOutPath = "/tmp/octopus-stage-out.tgz";
        const string StartMarkerPath = "/tmp/octopus-script-start";

        static readonly Regex CreateArtifactPattern = new(@"##octopus\[createArtifact\s+path='([^']*)'", RegexOptions.Compiled);
        static readonly string[] LocalOnlyWorkspaceFiles = { "Output.log", "scriptstate.json" };

        readonly AcaSandboxConfiguration config;
        readonly AcaSandboxClient client;
        readonly AcaSandboxBlobStore blobs;
        readonly IHomeDirectoryProvider home;
        readonly AcaSandboxPodImages podImages;
        readonly AcaSandboxToolSnapshots toolSnapshots;
        readonly AcaSandboxTokenProvider tokens;
        readonly AcaSandboxRegistry registry;
        readonly ISystemLog log;
        static readonly TimeSpan RecheckImageAfter = TimeSpan.FromMinutes(5);
        readonly ConcurrentDictionary<string, (string DiskImageId, DateTimeOffset CheckedAt)> diskImages = new();
        readonly ConcurrentDictionary<string, SemaphoreSlim> diskImageGates = new();

        public AcaSandboxScriptRunner(AcaSandboxConfiguration config, AcaSandboxClient client, AcaSandboxBlobStore blobs, IHomeDirectoryProvider home, AcaSandboxPodImages podImages, AcaSandboxToolSnapshots toolSnapshots, AcaSandboxTokenProvider tokens, AcaSandboxRegistry registry, ISystemLog log)
        {
            this.tokens = tokens;
            this.registry = registry;
            this.toolSnapshots = toolSnapshots;
            this.config = config;
            this.client = client;
            this.blobs = blobs;
            this.home = home;
            this.podImages = podImages;
            this.log = log;
        }

        public bool RunsEachScriptOnItsOwnMachine => true;

        // Package-cache commands read and write only the coordinator's Files folder and package journal, so a sandbox
        // would add its start-up and copy cost to a second of Calamari work.
        public bool ShouldRun(IScriptWorkspace workspace)
            => !File.Exists(workspace.BootstrapScriptFilePath)
                || !IsPackageCacheCommand(File.ReadAllText(workspace.BootstrapScriptFilePath));

        public async Task<int> RunAsync(
            IScriptWorkspace workspace,
            string shellPath,
            string taskId,
            IReadOnlyDictionary<string, string> environmentVariables,
            IScriptLogWriter writer,
            CancellationToken cancellationToken)
        {
            var homeDirectory = Path.GetFullPath(home.HomeDirectory ?? throw new InvalidOperationException("Tentacle home directory is not set."));
            void Verbose(string message) => writer.WriteOutput(ProcessOutputSource.Debug, message);
            LogUnmatchedPackageCacheBootstrap(workspace, Verbose);

            string? diskImageId = null;
            if (podImages.Get(workspace.ScriptTicket) is { } podImage && !IsAgentDefaultImage(podImage.Image!))
            {
                // A step in an execution container: its image arrives as the pod image, with the feed's credentials.
                diskImageId = await DiskImageForAsync(podImage.Image!, _ => Task.FromResult((podImage.FeedUsername, podImage.FeedPassword)), Verbose, cancellationToken);
                Verbose($"Pod image {podImage.Image} runs as sandbox disk image {diskImageId}");
            }

            diskImageId ??= config.Image == null
                ? config.DiskImageId
                : await DiskImageForAsync(config.Image, async ct => ((string?)AcaSandboxTokenProvider.RegistryUsername, (string?)await tokens.GetRegistryTokenAsync(config.Image.Split('/')[0], ct)), Verbose, cancellationToken);

            var clock = Stopwatch.StartNew();
            var referenced = FindReferencedHomePaths(workspace, homeDirectory)
                .Concat(RecentlyUploadedPackages(homeDirectory))
                .Distinct()
                .ToList();
            var toolsRoot = Path.Combine(homeDirectory, "Tools");
            var toolPaths = referenced.Where(p => p.StartsWith(toolsRoot + "/", StringComparison.Ordinal)).ToList();
            var snapshotId = toolPaths.Count > 0 && toolPaths.All(IsCompleteToolDirectory)
                ? toolSnapshots.TryGet(diskImageId, toolPaths)
                : null;
            // A sandbox started from a snapshot cannot mount Blob, so packages are copied in instead.
            var mounts = snapshotId == null
                ? await PlanMountsAsync(homeDirectory, referenced, Verbose, cancellationToken)
                : new Dictionary<string, string>();
            if (snapshotId != null)
                Verbose($"Starting from snapshot {snapshotId}, which has {string.Join(", ", toolPaths.Select(p => Path.GetRelativePath(toolsRoot, p)))} on local disk");
            if (mounts.Count > 0)
                Verbose($"Mounting {string.Join(", ", mounts.Values)} read-only from Blob (prepared in {clock.Elapsed.TotalSeconds:F1} s)");

            clock.Restart();
            var sandboxId = await client.CreateAsync(new Dictionary<string, string>
            {
                ["octopus-coordinator"] = config.CoordinatorName,
                ["octopus-ticket"] = workspace.ScriptTicket.TaskId,
                ["octopus-task"] = taskId
            }, diskImageId, mounts, cancellationToken, snapshotId);
            Verbose($"Sandbox {sandboxId} running after {clock.Elapsed.TotalSeconds:F1} s");

            try
            {
                clock.Restart();
                // Mounted folders, and tool folders already in the snapshot, need no copy and no check.
                var alreadyThere = mounts.Values.Concat(snapshotId != null ? toolPaths : Enumerable.Empty<string>()).ToList();
                var staged = await StageInAsync(sandboxId, workspace, referenced, alreadyThere, cancellationToken);
                Verbose($"Copied {staged.Files} files ({staged.Bytes / 1024.0 / 1024.0:F1} MB) into the sandbox in {clock.Elapsed.TotalSeconds:F1} s");

                int exitCode;
                clock.Restart();
                try
                {
                    exitCode = await RunScriptAsync(sandboxId, workspace, shellPath, homeDirectory, environmentVariables, writer, cancellationToken);
                    Verbose($"Script exited with code {exitCode} after {clock.Elapsed.TotalSeconds:F1} s");
                }
                finally
                {
                    // Also after a cancel: files the script wrote before it stopped still belong on the coordinator.
                    clock.Restart();
                    try
                    {
                        var returned = await StageOutAsync(sandboxId, homeDirectory, workspace.WorkingDirectory, mounts.Values.ToList(), CancellationToken.None);
                        Verbose($"Copied {returned} new or changed files back from the sandbox in {clock.Elapsed.TotalSeconds:F1} s");
                    }
                    catch (Exception ex)
                    {
                        // The script's own result stands; what it changed elsewhere (a database) has already happened.
                        writer.WriteOutput(ProcessOutputSource.StdErr, $"Could not copy changed files back from sandbox {sandboxId}: {ex.Message}");
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                return exitCode;
            }
            finally
            {
                if (config.KeepSandboxes)
                    Verbose($"Keeping sandbox {sandboxId} (ACA_SANDBOX_KEEP=true)");
                else
                    _ = DeleteInBackgroundAsync(sandboxId);
            }
        }

        /// <summary>
        /// The disk image built from what the image's tag points to now, so a moving tag such as latest follows each
        /// rebuild. The tag is looked up at most every five minutes; a new digest waits for its disk image to build. If
        /// the lookup or build fails, the disk image last used for the image stands, or after a restart the newest one
        /// built from the same repository.
        /// </summary>
        async Task<string> DiskImageForAsync(string image, Func<CancellationToken, Task<(string? Username, string? Password)>> credentials, Action<string> progress, CancellationToken cancellationToken)
        {
            if (diskImages.TryGetValue(image, out var known) && DateTimeOffset.UtcNow - known.CheckedAt < RecheckImageAfter)
                return known.DiskImageId;

            var gate = diskImageGates.GetOrAdd(image, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (diskImages.TryGetValue(image, out known) && DateTimeOffset.UtcNow - known.CheckedAt < RecheckImageAfter)
                    return known.DiskImageId;

                string? id;
                try
                {
                    var (username, password) = await credentials(cancellationToken);
                    var pinned = await registry.PinAsync(image, username, password, cancellationToken);
                    id = await client.EnsureDiskImageAsync(pinned, username, password, progress, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    id = known.DiskImageId ?? await client.FindNewestDiskImageAsync(AcaSandboxRegistry.RepositoryOf(image), cancellationToken);
                    if (id == null)
                        throw;
                    progress($"Could not check {image} for a newer version ({ex.Message}); keeping disk image {id}");
                    log.Warn(ex, $"Could not check {image} for a newer version; keeping disk image {id}");
                }

                if (id != known.DiskImageId)
                    log.Info($"Sandboxes for {image} start from disk image {id}");
                diskImages[image] = (id, DateTimeOffset.UtcNow);
                return id;
            }
            finally
            {
                gate.Release();
            }
        }

        // Deleting takes about 10 s; the script's result does not need to wait for it.
        async Task DeleteInBackgroundAsync(string sandboxId)
        {
            try
            {
                await client.DeleteAsync(sandboxId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                log.Warn(ex, $"Could not delete sandbox {sandboxId}; it is deleted after {config.IdleTimeout.TotalMinutes:F0} minutes idle.");
            }
        }

        static readonly string[] PackageCacheCommands = { "clean-packages", "find-package", "apply-delta", "release-package-lock", "register-package", "download-and-register-package" };
        static readonly Regex CalamariInvocation = new(@"^\s*(?:setsid\s+)?""\$CalamariExecutablePath""\s+(\S+)", RegexOptions.Multiline);

        // TEMPORARY: shows why a package-cache script reached a sandbox, so the matcher can be fixed from a real bootstrap.
        static void LogUnmatchedPackageCacheBootstrap(IScriptWorkspace workspace, Action<string> verbose)
        {
            if (!File.Exists(workspace.BootstrapScriptFilePath))
                return;
            var bootstrap = File.ReadAllText(workspace.BootstrapScriptFilePath);
            if (!PackageCacheCommands.Any(bootstrap.Contains))
                return;
            verbose($"Package-cache bootstrap sent to a sandbox ({CalamariInvocation.Matches(bootstrap).Count} matched invocations):");
            foreach (var line in bootstrap.Split('\n').Where(l => l.Contains("Calamari", StringComparison.OrdinalIgnoreCase) || PackageCacheCommands.Any(l.Contains)).Take(10))
            {
                var redacted = Regex.Replace(line, @"(?i)(password\S*\s+)\S+", "$1***");
                verbose("  " + redacted.Replace("\r", "\\r").Replace("\t", "\\t"));
            }
        }

        /// <summary>True when the bootstrap's only Calamari invocation is a package-cache command.</summary>
        internal static bool IsPackageCacheCommand(string bootstrap)
        {
            var invocations = CalamariInvocation.Matches(bootstrap);
            return invocations.Count == 1 && PackageCacheCommands.Contains(invocations[0].Groups[1].Value);
        }

        /// <summary>
        /// Decides which home folders the sandbox mounts read-only from Blob instead of receiving a copy: Tools when every
        /// tool folder the script refers to is complete (Success.txt) and in Blob, uploading it once if only the
        /// coordinator has it; Files when the script uses recently pushed packages (package-cache commands run locally).
        /// Returns volume name to mount path.
        /// </summary>
        async Task<Dictionary<string, string>> PlanMountsAsync(string homeDirectory, List<string> referenced, Action<string> verbose, CancellationToken cancellationToken)
        {
            var mounts = new Dictionary<string, string>();
            if (!blobs.Enabled)
                return mounts;

            var toolsRoot = Path.Combine(homeDirectory, "Tools");
            var toolPaths = referenced.Where(p => p.StartsWith(toolsRoot + "/", StringComparison.Ordinal)).ToList();
            var toolDirectories = toolPaths.Where(IsCompleteToolDirectory).ToList();
            // The mount hides the whole Tools folder, so a referenced path that cannot be uploaded whole means copying instead.
            if (toolPaths.Count > 0 && toolDirectories.Count == toolPaths.Count)
            {
                var allInBlob = true;
                foreach (var directory in toolDirectories)
                {
                    var prefix = Path.GetRelativePath(toolsRoot, directory);
                    try
                    {
                        if (!await blobs.ExistsAsync(config.ToolsContainer, prefix + "/Success.txt", cancellationToken))
                        {
                            var upload = Stopwatch.StartNew();
                            await blobs.UploadDirectoryAsync(config.ToolsContainer, prefix, directory, "Success.txt", cancellationToken);
                            verbose($"Uploaded {prefix} to Blob in {upload.Elapsed.TotalSeconds:F1} s");
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        verbose($"Could not put {prefix} in Blob, copying it instead: {ex.Message}");
                        allInBlob = false;
                    }
                }
                if (allInBlob)
                    mounts[config.ToolsVolume] = toolsRoot;
            }

            var filesRoot = Path.Combine(homeDirectory, "Files");
            var packages = referenced.Where(p => File.Exists(p) && p.StartsWith(filesRoot + "/", StringComparison.Ordinal)).ToList();
            if (packages.Count > 0)
            {
                try
                {
                    foreach (var package in packages)
                    {
                        var name = Path.GetRelativePath(filesRoot, package);
                        if (!await blobs.ExistsAsync(config.FilesContainer, name, cancellationToken))
                            await blobs.UploadFileAsync(config.FilesContainer, name, package, cancellationToken);
                    }
                    mounts[config.FilesVolume] = filesRoot;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    verbose($"Could not put packages in Blob, copying them instead: {ex.Message}");
                }
            }

            return mounts;
        }

        /// <summary>Uploads the files the script needs as one archive; the script's own exec unpacks it, saving a round trip.</summary>
        async Task<(int Files, long Bytes)> StageInAsync(string sandboxId, IScriptWorkspace workspace, List<string> referenced, List<string> presentRoots, CancellationToken cancellationToken)
        {
            bool Mounted(string path) => presentRoots.Any(root => path == root || path.StartsWith(root + "/", StringComparison.Ordinal));

            var files = Directory.EnumerateFiles(workspace.WorkingDirectory, "*", SearchOption.AllDirectories)
                .Where(f => !LocalOnlyWorkspaceFiles.Contains(Path.GetFileName(f)))
                .ToList();

            files.AddRange(referenced.Where(p => File.Exists(p) && !Mounted(p)));

            var directories = referenced.Where(p => Directory.Exists(p) && !Mounted(p)).ToList();
            if (directories.Count > 0)
            {
                // Directories (Calamari, tools) are usually baked into the disk image; copy only the missing ones.
                var check = string.Join("; ", directories.Select(d =>
                {
                    var marker = Path.Combine(d, "Success.txt");
                    return File.Exists(marker) ? $"[ -e {Quote(marker)} ] || echo {Quote(d)}" : $"[ -d {Quote(d)} ] || echo {Quote(d)}";
                }));
                var result = await client.RunAsync(sandboxId, check, cancellationToken);
                var missing = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var directory in missing)
                    files.AddRange(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories));
            }

            files = files.Distinct().ToList();

            var archive = Path.GetTempFileName();
            try
            {
                long bytes;
                await using (var output = File.Create(archive))
                {
                    await using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
                    await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
                    {
                        foreach (var file in files)
                            await tar.WriteEntryAsync(file, file.TrimStart('/'), cancellationToken);
                        await WriteKubectlStubAsync(tar, cancellationToken);
                    }
                }

                bytes = new FileInfo(archive).Length;
                await using (var input = File.OpenRead(archive))
                {
                    await client.UploadFileAsync(sandboxId, StageInPath, input, cancellationToken);
                }

                return (files.Count, bytes);
            }
            finally
            {
                File.Delete(archive);
            }
        }

        /// <summary>
        /// Absolute paths under the Tentacle home that the bootstrap or other plain-text workspace files mention,
        /// such as Tools/Calamari.linux-x64/{version}. Encrypted variable files are not read.
        /// </summary>
        static List<string> FindReferencedHomePaths(IScriptWorkspace workspace, string homeDirectory)
        {
            var text = new StringBuilder();
            foreach (var file in Directory.EnumerateFiles(workspace.WorkingDirectory))
            {
                if (file.EndsWith(".secret", StringComparison.OrdinalIgnoreCase) || new FileInfo(file).Length > 1024 * 1024)
                    continue;
                text.AppendLine(File.ReadAllText(file));
            }

            var all = text.ToString().Replace("$TentacleHome", homeDirectory).Replace("${TentacleHome}", homeDirectory);
            var pattern = new Regex(Regex.Escape(homeDirectory.TrimEnd('/')) + @"/(?:Files|Tools)/[^""'\s\\;]+");
            return pattern.Matches(all)
                .Select(m => m.Value.TrimEnd('.', ',', ')'))
                .Where(p => File.Exists(p) || Directory.Exists(p))
                .Distinct()
                .ToList();
        }

        /// <summary>
        /// Packages Octopus pushed to the coordinator's Files folder recently. A sandbox never holds a package cache, so
        /// Octopus uploads every package a task needs during that task; uploads carry no task id, so a time window stands in
        /// for "this task". Package clean-up runs in sandboxes and never deletes the coordinator's copies, so old ones are
        /// pruned here.
        /// </summary>
        IEnumerable<string> RecentlyUploadedPackages(string homeDirectory)
        {
            var files = Path.Combine(homeDirectory, "Files");
            if (!Directory.Exists(files))
                return Array.Empty<string>();

            var recent = new List<string>();
            foreach (var file in Directory.EnumerateFiles(files, "*", SearchOption.AllDirectories))
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(file);
                if (age <= config.PackageWindow)
                    recent.Add(file);
                else if (age > TimeSpan.FromDays(1))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        log.Verbose($"Could not prune old package {file}: {ex.Message}");
                    }
                }
            }
            return recent;
        }

        async Task<int> RunScriptAsync(
            string sandboxId,
            IScriptWorkspace workspace,
            string shellPath,
            string homeDirectory,
            IReadOnlyDictionary<string, string> environmentVariables,
            IScriptLogWriter writer,
            CancellationToken cancellationToken)
        {
            var arguments = string.Join(" ", (workspace.ScriptArguments ?? Array.Empty<string>()).Select(Quote));
            var command = $"tar -xzf {StageInPath} -C / && rm -f {StageInPath} && touch {StartMarkerPath} && cd {Quote(workspace.WorkingDirectory)} && exec {Quote(shellPath)} {Quote(workspace.BootstrapScriptFilePath)} {arguments}";

            var environment = LocalScriptEnvironment(homeDirectory);
            foreach (var pair in environmentVariables)
                environment[pair.Key] = pair.Value;

            var stdout = new LineSplitter(line => OnLineAsync(sandboxId, workspace.WorkingDirectory, ProcessOutputSource.StdOut, line, writer));
            var stderr = new LineSplitter(line => OnLineAsync(sandboxId, workspace.WorkingDirectory, ProcessOutputSource.StdErr, line, writer));

            // The exec socket must stay open while the script runs: dropping it kills the process with SIGKILL.
            // Cancellation therefore sends SIGTERM (the bootstrap traps it and stops Calamari), and only drops
            // the socket if the script has not exited after the grace period.
            using var dropSocket = new CancellationTokenSource();
            using var onCancel = cancellationToken.Register(() => _ = Task.Run(async () =>
            {
                try
                {
                    writer.WriteOutput(ProcessOutputSource.Debug, $"Cancelling: sending SIGTERM to the script in sandbox {sandboxId}");
                    await client.RunAsync(sandboxId, $"pkill -TERM -f {Quote(workspace.BootstrapScriptFilePath)} || true", CancellationToken.None);
                }
                catch (Exception ex)
                {
                    log.Warn(ex, $"Could not signal the script in sandbox {sandboxId}");
                }
                dropSocket.CancelAfter(config.CancelGracePeriod);
            }));

            var exitCode = await client.ExecAsync(sandboxId,
                "/bin/bash",
                new[] { "-c", command },
                environment,
                (isError, bytes) => (isError ? stderr : stdout).WriteAsync(bytes),
                dropSocket.Token);

            await stdout.FlushAsync();
            await stderr.FlushAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return exitCode;
        }

        // The Kubernetes health check reads an agent-metrics configmap with kubectl and logs an error when kubectl is
        // missing. A sandbox has no cluster: answer that read with nothing and refuse every other command loudly.
        const string KubectlStub = "#!/bin/sh\n"
            + "if [ \"$1\" = get ] && [ \"$2\" = cm ]; then exit 0; fi\n"
            + "echo \"kubectl is not available on sandbox workers\" >&2\n"
            + "exit 127\n";

        static async Task WriteKubectlStubAsync(TarWriter tar, CancellationToken cancellationToken)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "usr/local/bin/kubectl")
            {
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(KubectlStub))
            };
            await tar.WriteEntryAsync(entry, cancellationToken);
        }

        // The Kubernetes agent's own tools image stands for "no execution container": use the default disk image.
        static bool IsAgentDefaultImage(string image) => image.Contains("kubernetes-agent-tools-base", StringComparison.OrdinalIgnoreCase);

        // A local script inherits the Tentacle process's own variables (TentacleHome, TentacleVersion, proxy settings, ...).
        Dictionary<string, string> LocalScriptEnvironment(string homeDirectory)
        {
            var environment = new Dictionary<string, string>
            {
                ["OCTOPUS_RUNNING_IN_CONTAINER"] = "Y"
            };
            foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
            {
                var name = (string)variable.Key;
                if (name.StartsWith("Tentacle", StringComparison.Ordinal) || name is "CalamariPackageRetentionJournalPath" or "AgentProgramDirectoryPath")
                    environment[name] = (string?)variable.Value ?? "";
            }
            environment["TentacleHome"] = homeDirectory;
            // A Kubernetes agent's script pod gets no deployment journal.
            environment.Remove("TentacleJournal");
            foreach (var name in config.PassThroughVariables)
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (value != null)
                    environment[name] = value;
            }
            return environment;
        }

        async Task OnLineAsync(string sandboxId, string workingDirectory, ProcessOutputSource source, string line, IScriptLogWriter writer)
        {
            var artifact = CreateArtifactPattern.Match(line);
            if (artifact.Success)
            {
                var path = DecodeServiceMessageValue(artifact.Groups[1].Value);
                if (!IsArtifactPathAllowed(path, workingDirectory, Path.GetTempPath()))
                {
                    writer.WriteOutput(ProcessOutputSource.StdErr, $"Not copying artifact {path} back from sandbox {sandboxId}: only the script's work folder and the temp folder are allowed.");
                }
                else
                {
                    var target = NormalizeUnixPath(path);
                    var download = target + ".sandbox-download";
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        await using (var file = File.Create(download))
                        {
                            await client.DownloadFileAsync(sandboxId, target, file, CancellationToken.None);
                        }
                        File.Move(download, target, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        File.Delete(download);
                        writer.WriteOutput(ProcessOutputSource.StdErr, $"Could not copy artifact {path} back from sandbox {sandboxId}: {ex.Message}");
                    }
                }
            }

            writer.WriteOutput(source, line);
        }

        /// <summary>Artifacts are taken only from the script's own work folder or the temp folder.</summary>
        internal static bool IsArtifactPathAllowed(string path, string workingDirectory, string tempDirectory)
        {
            if (!path.StartsWith("/", StringComparison.Ordinal))
                return false;
            var full = NormalizeUnixPath(path);
            return IsUnder(full, workingDirectory) || IsUnder(full, tempDirectory);
        }

        /// <summary>
        /// What a sandbox may write back into the coordinator's home: this script's work folder, tools, packages and
        /// Calamari's journals. Never the Tentacle's configuration, certificates or logs.
        /// </summary>
        internal static bool IsCopyBackAllowed(string path, string homeDirectory, string workingDirectory)
        {
            var full = NormalizeUnixPath(path);
            var home = homeDirectory.TrimEnd('/');
            if (IsUnder(full, workingDirectory) || IsUnder(full, home + "/Tools") || IsUnder(full, home + "/Files"))
                return true;
            return full == home + "/DeploymentJournal.xml" || full == home + "/PackageRetentionJournal.json";
        }

        /// <summary>Resolves "." and ".." in an absolute sandbox path, the same way on any host.</summary>
        internal static string NormalizeUnixPath(string path)
        {
            var parts = new List<string>();
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == "..")
                {
                    if (parts.Count > 0)
                        parts.RemoveAt(parts.Count - 1);
                }
                else if (part != ".")
                {
                    parts.Add(part);
                }
            }
            return "/" + string.Join("/", parts);
        }

        static bool IsUnder(string fullPath, string directory)
            => fullPath.StartsWith(directory.TrimEnd('/') + "/", StringComparison.Ordinal);

        static bool IsCompleteToolDirectory(string path) => Directory.Exists(path) && File.Exists(Path.Combine(path, "Success.txt"));

        async Task<int> StageOutAsync(string sandboxId, string homeDirectory, string workingDirectory, List<string> mountedRoots, CancellationToken cancellationToken)
        {
            // Read-only mounts never change; pruning them also avoids listing Blob.
            var prune = string.Concat(mountedRoots.Select(root => $"-path {Quote(root)} -prune -o "));
            var pack = await client.RunAsync(sandboxId,
                $"cd / && find {Quote(homeDirectory)} {prune}-cnewer {StartMarkerPath} -type f -printf '%P\\n' | wc -l && find {Quote(homeDirectory)} {prune}-cnewer {StartMarkerPath} -type f -print0 | tar -czf {StageOutPath} --null -T - 2>/dev/null",
                cancellationToken);
            if (pack.ExitCode != 0)
                throw new IOException($"Packing changed files in sandbox {sandboxId} failed ({pack.ExitCode}): {pack.StdErr}");

            var count = int.TryParse(pack.StdOut.Trim(), out var n) ? n : 0;
            if (count == 0)
                return 0;

            var archive = Path.GetTempFileName();
            try
            {
                await using (var file = File.Create(archive))
                {
                    await client.DownloadFileAsync(sandboxId, StageOutPath, file, cancellationToken);
                }

                await using var input = File.OpenRead(archive);
                await using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var tar = new TarReader(gzip);
                while (await tar.GetNextEntryAsync(cancellationToken: cancellationToken) is { } entry)
                {
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                        continue;

                    var destination = NormalizeUnixPath("/" + entry.Name);
                    if (!IsCopyBackAllowed(destination, homeDirectory, workingDirectory) || LocalOnlyWorkspaceFiles.Contains(Path.GetFileName(destination)))
                        continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await entry.ExtractToFileAsync(destination, overwrite: true, cancellationToken);
                }

                return count;
            }
            finally
            {
                File.Delete(archive);
            }
        }

        static string DecodeServiceMessageValue(string value)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch (FormatException)
            {
                return value;
            }
        }

        static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

        /// <summary>Turns streamed byte chunks into whole lines.</summary>
        class LineSplitter
        {
            readonly Func<string, Task> onLine;
            readonly Decoder decoder = Encoding.UTF8.GetDecoder();
            readonly StringBuilder pending = new();

            public LineSplitter(Func<string, Task> onLine)
            {
                this.onLine = onLine;
            }

            public async Task WriteAsync(byte[] bytes)
            {
                var chars = new char[decoder.GetCharCount(bytes, 0, bytes.Length)];
                decoder.GetChars(bytes, 0, bytes.Length, chars, 0);
                foreach (var c in chars)
                {
                    if (c == '\n')
                    {
                        var line = pending.ToString().TrimEnd('\r');
                        pending.Clear();
                        await onLine(line);
                    }
                    else
                    {
                        pending.Append(c);
                    }
                }
            }

            public async Task FlushAsync()
            {
                if (pending.Length > 0)
                {
                    var line = pending.ToString();
                    pending.Clear();
                    await onLine(line);
                }
            }
        }
    }
}
#endif
