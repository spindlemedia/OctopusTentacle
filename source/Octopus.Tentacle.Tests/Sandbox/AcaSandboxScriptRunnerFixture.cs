#if !NETFRAMEWORK
using System;
using FluentAssertions;
using NUnit.Framework;
using Octopus.Tentacle.Sandbox;

namespace Octopus.Tentacle.Tests.Sandbox
{
    [TestFixture]
    public class AcaSandboxScriptRunnerFixture
    {
        const string Home = "/etc/octopus";
        const string Work = "/etc/octopus/Work/20261003-ticket";

        [TestCase("/etc/octopus/Work/20261003-ticket/out/report.html", true)]
        [TestCase("/tmp/deploy-1/Update.sql", true)]
        [TestCase("/etc/octopus/tentacle.config", false)]
        [TestCase("/etc/octopus/Work/20261003-ticket/../../tentacle.config", false)]
        [TestCase("/etc/octopus/Work/other-ticket/file.txt", false)]
        [TestCase("/tmp/../etc/passwd", false)]
        [TestCase("relative/file.txt", false)]
        public void ArtifactsComeOnlyFromTheWorkFolderOrTemp(string path, bool allowed)
        {
            AcaSandboxScriptRunner.IsArtifactPathAllowed(path, Work, "/tmp/").Should().Be(allowed);
        }

        [TestCase("/etc/octopus/Work/20261003-ticket/Output/x.txt", true)]
        [TestCase("/etc/octopus/Tools/Calamari.linux-x64/2026.4.55/Success.txt", true)]
        [TestCase("/etc/octopus/Files/Example.Package@S1.2.3@abc.nupkg", true)]
        [TestCase("/etc/octopus/DeploymentJournal.xml", true)]
        [TestCase("/etc/octopus/PackageRetentionJournal.json", true)]
        [TestCase("/etc/octopus/tentacle.config", false)]
        [TestCase("/etc/octopus/Tentacle/Instances/tentacle.config", false)]
        [TestCase("/etc/octopus/Logs/OctopusTentacle.txt", false)]
        [TestCase("/etc/octopus/Work/other-ticket/Bootstrap.sh", false)]
        [TestCase("/etc/octopus/Tools/../tentacle.config", false)]
        public void CopyBackWritesOnlyScriptOutputsToolsPackagesAndJournals(string path, bool allowed)
        {
            AcaSandboxScriptRunner.IsCopyBackAllowed(path, Home, Work).Should().Be(allowed);
        }

        [TestCase("setsid  \"$CalamariExecutablePath\" find-package -packageId \"A\" &\nwait $CALAMARI_PID\n", true)]
        [TestCase("setsid  \"$CalamariExecutablePath\" release-package-lock -taskId \"ServerTasks-1\" &\n", true)]
        [TestCase("  \"$CalamariExecutablePath\" clean-packages\n", true)]
        [TestCase("setsid  \"$CalamariExecutablePath\" run-script -script \"Script.sh\" &\n", false)]
        [TestCase("echo ' find-package '\n", false)]
        [TestCase("\"$CalamariExecutablePath\" find-package\n\"$CalamariExecutablePath\" run-script\n", false)]
        public void OnlyABootstrapWhoseOneCalamariCallManagesThePackageCacheRunsLocally(string bootstrap, bool local)
        {
            AcaSandboxScriptRunner.IsPackageCacheCommand(bootstrap).Should().Be(local);
        }

        [TestCase("smiregistry.azurecr.io/smi.octo.dbworker:latest", "smiregistry.azurecr.io", "smi.octo.dbworker", "latest", null)]
        [TestCase("smiregistry.azurecr.io/smi.octo.dbworker", "smiregistry.azurecr.io", "smi.octo.dbworker", "latest", null)]
        [TestCase("localhost:5000/team/tool:1.2", "localhost:5000", "team/tool", "1.2", null)]
        [TestCase("ubuntu:24.04", "docker.io", "library/ubuntu", "24.04", null)]
        [TestCase("octopusdeploy/worker-tools", "docker.io", "octopusdeploy/worker-tools", "latest", null)]
        [TestCase("ghcr.io/a/b@sha256:abc", "ghcr.io", "a/b", "latest", "sha256:abc")]
        public void ParsesImageReferences(string image, string host, string repository, string tag, string? digest)
        {
            AcaSandboxRegistry.ImageReference.Parse(image).Should().Be(new AcaSandboxRegistry.ImageReference(host, repository, tag, digest));
        }

        [TestCase("/a/./b/../c", "/a/c")]
        [TestCase("/../../etc", "/etc")]
        [TestCase("//a//b/", "/a/b")]
        public void NormalizesSandboxPaths(string path, string expected)
        {
            AcaSandboxScriptRunner.NormalizeUnixPath(path).Should().Be(expected);
        }

        [Test]
        public void ToolSnapshotKeyIgnoresOrderAndTrailingSlashButNotTheDiskImage()
        {
            var a = AcaSandboxToolSnapshots.Key("disk-1", new[] { "/etc/octopus/Tools/A/1", "/etc/octopus/Tools/B/2/" });
            AcaSandboxToolSnapshots.Key("disk-1", new[] { "/etc/octopus/Tools/B/2", "/etc/octopus/Tools/A/1" }).Should().Be(a);
            AcaSandboxToolSnapshots.Key("disk-2", new[] { "/etc/octopus/Tools/A/1", "/etc/octopus/Tools/B/2" }).Should().NotBe(a);
            AcaSandboxToolSnapshots.Key("disk-1", new[] { "/etc/octopus/Tools/A/1", "/etc/octopus/Tools/B/3" }).Should().NotBe(a);
        }

        [TestCase(23, 24, false)]
        [TestCase(25, 24, true)]
        [TestCase(1000, 0, false)]
        public void ToolSnapshotIsStaleOnlyPastAPositiveMaxAge(int ageHours, int maxAgeHours, bool stale)
        {
            var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
            AcaSandboxToolSnapshots.IsStale(now.AddHours(-ageHours), now, TimeSpan.FromHours(maxAgeHours)).Should().Be(stale);
        }
    }
}
#endif
