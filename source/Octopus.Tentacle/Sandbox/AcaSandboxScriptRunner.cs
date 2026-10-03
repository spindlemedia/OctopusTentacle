#if !NETFRAMEWORK
using System;
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
        readonly IHomeDirectoryProvider home;
        readonly ISystemLog log;
        int orphansCleaned;

        public AcaSandboxScriptRunner(AcaSandboxConfiguration config, AcaSandboxClient client, IHomeDirectoryProvider home, ISystemLog log)
        {
            this.config = config;
            this.client = client;
            this.home = home;
            this.log = log;
        }

        public bool RunsEachScriptOnItsOwnMachine => true;

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

            await CleanOrphansOnceAsync(cancellationToken);

            var imagePull = ContainerImagePull.TryParse(workspace.BootstrapScriptFilePath);
            if (imagePull != null)
            {
                // Execution containers: Octopus first asks the worker to `docker pull` the step's image. A sandbox has no
                // Docker daemon, so the coordinator builds (or reuses) a sandbox disk image from that image instead.
                var clockPull = Stopwatch.StartNew();
                var pulledDiskImageId = await client.EnsureDiskImageAsync(imagePull.Image, imagePull.Username, imagePull.Password, m => writer.WriteOutput(ProcessOutputSource.StdOut, m), cancellationToken);
                writer.WriteOutput(ProcessOutputSource.StdOut, $"Sandbox disk image {pulledDiskImageId} is ready for {imagePull.Image} ({clockPull.Elapsed.TotalSeconds:F1} s)");
                // The server reads these from Calamari's download-and-register-package (PackageDownloadService.SetOutputVariables).
                // Same shapes as Calamari's Docker downloader: an image digest and a byte size.
                var pseudoDigest = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(imagePull.Image + "|" + pulledDiskImageId))).ToLowerInvariant();
                writer.WriteOutput(ProcessOutputSource.StdOut, SetVariableMessage("StagedPackage.Hash", pseudoDigest));
                writer.WriteOutput(ProcessOutputSource.StdOut, SetVariableMessage("StagedPackage.Size", "1048576"));
                writer.WriteOutput(ProcessOutputSource.StdOut, SetVariableMessage("StagedPackage.FullPathOnRemoteMachine", ""));
                return 0;
            }

            string? diskImageId = null;
            var container = ContainerStep.TryUnwrap(workspace.BootstrapScriptFilePath);
            if (container != null)
            {
                // Execution container step: run Calamari directly in a sandbox built from the step's image.
                diskImageId = await client.EnsureDiskImageAsync(container.Image, null, null, Verbose, cancellationToken);
                Verbose($"Execution container {container.Image} runs as sandbox disk image {diskImageId}");
            }

            var clock = Stopwatch.StartNew();
            var sandboxId = await client.CreateAsync(new Dictionary<string, string>
            {
                ["octopus-coordinator"] = config.CoordinatorName,
                ["octopus-ticket"] = workspace.ScriptTicket.TaskId,
                ["octopus-task"] = taskId
            }, diskImageId, cancellationToken);
            Verbose($"Sandbox {sandboxId} running after {clock.Elapsed.TotalSeconds:F1} s");

            try
            {
                clock.Restart();
                var staged = await StageInAsync(sandboxId, workspace, homeDirectory, taskId, cancellationToken);
                Verbose($"Copied {staged.Files} files ({staged.Bytes / 1024.0 / 1024.0:F1} MB) into the sandbox in {clock.Elapsed.TotalSeconds:F1} s");

                clock.Restart();
                var exitCode = await RunScriptAsync(sandboxId, workspace, shellPath, homeDirectory, container?.Environment, environmentVariables, writer, cancellationToken);
                Verbose($"Script exited with code {exitCode} after {clock.Elapsed.TotalSeconds:F1} s");

                clock.Restart();
                var returned = await StageOutAsync(sandboxId, homeDirectory, CancellationToken.None);
                Verbose($"Copied {returned} new or changed files back from the sandbox in {clock.Elapsed.TotalSeconds:F1} s");

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

        // Deleting takes about 10 s; the script's result does not need to wait for it.
        async Task DeleteInBackgroundAsync(string sandboxId)
        {
            try
            {
                await client.DeleteAsync(sandboxId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                log.Warn(ex, $"Could not delete sandbox {sandboxId}; the orphan cleaner will retry on the next start.");
            }
        }

        async Task<(int Files, long Bytes)> StageInAsync(string sandboxId, IScriptWorkspace workspace, string homeDirectory, string taskId, CancellationToken cancellationToken)
        {
            var files = Directory.EnumerateFiles(workspace.WorkingDirectory, "*", SearchOption.AllDirectories)
                .Where(f => !LocalOnlyWorkspaceFiles.Contains(Path.GetFileName(f)))
                .ToList();

            var referenced = FindReferencedHomePaths(workspace, homeDirectory)
                .Concat(RecentlyUploadedPackages(homeDirectory))
                .ToList();
            files.AddRange(referenced.Where(File.Exists));

            var directories = referenced.Where(Directory.Exists).ToList();
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
                    }
                }

                bytes = new FileInfo(archive).Length;
                await using (var input = File.OpenRead(archive))
                {
                    await client.UploadFileAsync(sandboxId, StageInPath, input, cancellationToken);
                }

                var extract = await client.RunAsync(sandboxId,
                    $"tar -xzf {StageInPath} -C / && rm -f {StageInPath} && touch {StartMarkerPath}",
                    cancellationToken);
                if (extract.ExitCode != 0)
                    throw new IOException($"Extracting the staged files in sandbox {sandboxId} failed ({extract.ExitCode}): {extract.StdErr}");

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
                    File.Delete(file);
            }
            return recent;
        }

        async Task<int> RunScriptAsync(
            string sandboxId,
            IScriptWorkspace workspace,
            string shellPath,
            string homeDirectory,
            IReadOnlyDictionary<string, string>? containerEnvironment,
            IReadOnlyDictionary<string, string> environmentVariables,
            IScriptLogWriter writer,
            CancellationToken cancellationToken)
        {
            var arguments = string.Join(" ", (workspace.ScriptArguments ?? Array.Empty<string>()).Select(Quote));
            var command = $"cd {Quote(workspace.WorkingDirectory)} && exec {Quote(shellPath)} {Quote(workspace.BootstrapScriptFilePath)} {arguments}";

            var localEnvironment = LocalScriptEnvironment(homeDirectory);
            // docker run's --env values are shell expressions such as $TentacleHome; expand them against the local environment.
            var environment = containerEnvironment != null
                ? containerEnvironment.ToDictionary(
                    pair => pair.Key,
                    pair => Regex.Replace(pair.Value, @"\$\{?([A-Za-z_][A-Za-z0-9_]*)\}?", m => localEnvironment.TryGetValue(m.Groups[1].Value, out var value) ? value : ""))
                : localEnvironment;
            // The bootstrap itself runs outside the container on a normal worker and always needs the home.
            environment["TentacleHome"] = homeDirectory;
            foreach (var pair in environmentVariables)
                environment[pair.Key] = pair.Value;

            var stdout = new LineSplitter(line => OnLineAsync(sandboxId, ProcessOutputSource.StdOut, line, writer));
            var stderr = new LineSplitter(line => OnLineAsync(sandboxId, ProcessOutputSource.StdErr, line, writer));

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
            foreach (var name in config.PassThroughVariables)
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (value != null)
                    environment[name] = value;
            }
            return environment;
        }

        async Task OnLineAsync(string sandboxId, ProcessOutputSource source, string line, IScriptLogWriter writer)
        {
            var artifact = CreateArtifactPattern.Match(line);
            if (artifact.Success)
            {
                var path = DecodeServiceMessageValue(artifact.Groups[1].Value);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await using var file = File.Create(path);
                    await client.DownloadFileAsync(sandboxId, path, file, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    writer.WriteOutput(ProcessOutputSource.StdErr, $"Could not copy artifact {path} back from sandbox {sandboxId}: {ex.Message}");
                }
            }

            writer.WriteOutput(source, line);
        }

        async Task<int> StageOutAsync(string sandboxId, string homeDirectory, CancellationToken cancellationToken)
        {
            var pack = await client.RunAsync(sandboxId,
                $"cd / && find {Quote(homeDirectory)} -cnewer {StartMarkerPath} -type f -printf '%P\\n' | wc -l && find {Quote(homeDirectory)} -cnewer {StartMarkerPath} -type f -print0 | tar -czf {StageOutPath} --null -T - 2>/dev/null",
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
                var root = homeDirectory.TrimEnd('/') + "/";
                while (await tar.GetNextEntryAsync(cancellationToken: cancellationToken) is { } entry)
                {
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                        continue;

                    var destination = Path.GetFullPath("/" + entry.Name.TrimStart('/'));
                    if (!destination.StartsWith(root, StringComparison.Ordinal) || LocalOnlyWorkspaceFiles.Contains(Path.GetFileName(destination)))
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

        async Task CleanOrphansOnceAsync(CancellationToken cancellationToken)
        {
            // Sandboxes left behind by an earlier run of this coordinator (crash or restart) are deleted once at start-up.
            if (config.KeepSandboxes || Interlocked.Exchange(ref orphansCleaned, 1) == 1)
                return;

            try
            {
                foreach (var sandbox in await client.ListAsync(cancellationToken))
                {
                    if (sandbox["labels"]?.Value<string>("octopus-coordinator") != config.CoordinatorName)
                        continue;
                    var id = sandbox.Value<string>("id")!;
                    log.Info($"Deleting orphaned sandbox {id} from an earlier run of {config.CoordinatorName}");
                    await client.DeleteAsync(id, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                log.Warn(ex, "Could not clean up orphaned sandboxes");
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

        static string SetVariableMessage(string name, string value)
            => $"##octopus[setVariable name='{Convert.ToBase64String(Encoding.UTF8.GetBytes(name))}' value='{Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}']";

        static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

        /// <summary>
        /// The bootstrap of an execution-container step wraps Calamari in a multi-line
        /// <c>docker run --entrypoint='' --rm \ ... image \</c> (one argument per line) followed by the usual Calamari line.
        /// </summary>
        static class ContainerStep
        {
            static readonly Regex DockerRun = new(@"^docker run [^\n]*\\\n(?:[^\n]*\\\n)*", RegexOptions.Multiline);

            static readonly Regex EnvArgument = new(@"--env\s+[""']?([A-Za-z_][A-Za-z0-9_]*)=([^\s""']*)[""']?");

            /// <summary>
            /// Removes the docker run wrapper from the bootstrap and returns the image plus only the variables docker run
            /// would pass (Calamari journals when it sees TentacleJournal, which a container never has), or null.
            /// </summary>
            public static ContainerRun? TryUnwrap(string bootstrapPath)
            {
                if (!File.Exists(bootstrapPath))
                    return null;
                var text = File.ReadAllText(bootstrapPath);
                var match = DockerRun.Match(text);
                if (!match.Success)
                    return null;

                var lines = match.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var image = lines[^1].TrimEnd('\\').Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Last();
                var environment = EnvArgument.Matches(match.Value).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
                File.WriteAllText(bootstrapPath, text.Remove(match.Index, match.Length));
                return new ContainerRun(image, environment);
            }
        }

        record ContainerRun(string Image, IReadOnlyDictionary<string, string> Environment);

        /// <summary>A Calamari download-and-register-package call for a Docker feed, read from the bootstrap script.</summary>
        class ContainerImagePull
        {
            public string Image { get; private init; } = "";
            public string? Username { get; private init; }
            public string? Password { get; private init; }

            public static ContainerImagePull? TryParse(string bootstrapPath)
            {
                if (!File.Exists(bootstrapPath))
                    return null;
                var line = File.ReadLines(bootstrapPath).FirstOrDefault(l => l.Contains(" download-and-register-package ") && l.Contains("-feedType \"Docker\""));
                if (line == null)
                    return null;

                string? Arg(string name)
                {
                    var match = Regex.Match(line, "-" + name + @"\s+""([^""]*)""");
                    return match.Success ? match.Groups[1].Value : null;
                }

                var packageId = Arg("packageId") ?? throw new InvalidOperationException("download-and-register-package without -packageId");
                var version = Arg("packageVersion") ?? throw new InvalidOperationException("download-and-register-package without -packageVersion");
                var host = new Uri(Arg("feedUri") ?? "https://index.docker.io").Host;
                var image = host is "index.docker.io" or "registry-1.docker.io" or "docker.io"
                    ? $"docker.io/{(packageId.Contains('/') ? packageId : "library/" + packageId)}:{version}"
                    : $"{host}/{packageId}:{version}";

                return new ContainerImagePull { Image = image, Username = Arg("feedUsername"), Password = Arg("feedPassword") };
            }
        }

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
