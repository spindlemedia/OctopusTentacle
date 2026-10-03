#if !NETFRAMEWORK
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Octopus.Tentacle.Core.Diagnostics;

namespace Octopus.Tentacle.Sandbox
{
    /// <summary>
    /// Snapshots of a disk image with the tool folders a script uses (Calamari) already on local disk. Calamari read from
    /// the Blob mount takes about 3 s to start in each new sandbox; from local disk about 0.5 s. A snapshot is built in the
    /// background the first time a disk image and set of tools is seen, and found by label by other coordinators.
    /// </summary>
    public class AcaSandboxToolSnapshots
    {
        const string ToolsLabel = "octopus-tools";
        const string DiskImageLabel = "octopus-disk-image";

        readonly AcaSandboxConfiguration config;
        readonly AcaSandboxClient client;
        readonly ISystemLog log;
        readonly ConcurrentDictionary<string, Task<string?>> snapshots = new();

        public AcaSandboxToolSnapshots(AcaSandboxConfiguration config, AcaSandboxClient client, ISystemLog log)
        {
            this.config = config;
            this.client = client;
            this.log = log;
        }

        /// <summary>The snapshot for these tools, or null while it is being built (or could not be).</summary>
        public string? TryGet(string diskImageId, IReadOnlyList<string> toolDirectories)
        {
            if (!config.ToolSnapshots)
                return null;
            var key = Key(diskImageId, toolDirectories);
            var snapshot = snapshots.GetOrAdd(key, _ => Task.Run(() => FindOrBuildAsync(key, diskImageId, toolDirectories)));
            return snapshot.IsCompletedSuccessfully ? snapshot.Result : null;
        }

        internal static string Key(string diskImageId, IEnumerable<string> toolDirectories)
        {
            var text = diskImageId + "|" + string.Join("|", toolDirectories.Select(d => d.TrimEnd('/')).OrderBy(d => d, StringComparer.Ordinal));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).Substring(0, 32).ToLowerInvariant();
        }

        async Task<string?> FindOrBuildAsync(string key, string diskImageId, IReadOnlyList<string> toolDirectories)
        {
            try
            {
                var existing = await client.ListSnapshotsAsync(CancellationToken.None);
                var match = existing.FirstOrDefault(s => s["labels"]?.Value<string>(ToolsLabel) == key);
                if (match != null)
                    return match.Value<string>("id");

                var clock = Stopwatch.StartNew();
                var sandboxId = await client.CreateAsync(new Dictionary<string, string>
                {
                    ["octopus-coordinator"] = config.CoordinatorName,
                    ["octopus-tools-build"] = key
                }, diskImageId, new Dictionary<string, string>(), CancellationToken.None);
                string snapshotId;
                try
                {
                    await CopyInAsync(sandboxId, toolDirectories);
                    snapshotId = await client.CreateSnapshotAsync(sandboxId, new Dictionary<string, string> { [ToolsLabel] = key, [DiskImageLabel] = diskImageId }, CancellationToken.None);
                }
                finally
                {
                    try { await client.DeleteAsync(sandboxId, CancellationToken.None); }
                    catch (Exception ex) { log.Warn(ex, $"Could not delete snapshot builder sandbox {sandboxId}"); }
                }
                log.Info($"Built sandbox snapshot {snapshotId} of disk image {diskImageId} with {string.Join(", ", toolDirectories)} in {clock.Elapsed.TotalSeconds:F1} s");

                // The server no longer sends older tool versions, so their snapshots on this disk image can go.
                foreach (var old in existing.Where(s => s["labels"]?.Value<string>(DiskImageLabel) == diskImageId && s["labels"]?.Value<string>(ToolsLabel) != key))
                {
                    try { await client.DeleteSnapshotAsync(old.Value<string>("id")!, CancellationToken.None); }
                    catch (Exception ex) { log.Warn(ex, $"Could not delete old sandbox snapshot {old["id"]}"); }
                }
                return snapshotId;
            }
            catch (Exception ex)
            {
                log.Warn(ex, $"Could not build a sandbox snapshot with {string.Join(", ", toolDirectories)}; scripts mount or copy the tools instead");
                return null;
            }
        }

        async Task CopyInAsync(string sandboxId, IReadOnlyList<string> toolDirectories)
        {
            const string remote = "/tmp/octopus-tools.tgz";
            var archive = Path.GetTempFileName();
            try
            {
                await using (var output = File.Create(archive))
                await using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
                await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
                {
                    foreach (var file in toolDirectories.SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)))
                        await tar.WriteEntryAsync(file, file.TrimStart('/'), CancellationToken.None);
                }
                await using (var input = File.OpenRead(archive))
                    await client.UploadFileAsync(sandboxId, remote, input, CancellationToken.None);
                var extract = await client.RunAsync(sandboxId, $"tar -xzf {remote} -C / && rm -f {remote}", CancellationToken.None);
                if (extract.ExitCode != 0)
                    throw new IOException($"Extracting tools in sandbox {sandboxId} failed ({extract.ExitCode}): {extract.StdErr}");
            }
            finally
            {
                File.Delete(archive);
            }
        }
    }
}
#endif
