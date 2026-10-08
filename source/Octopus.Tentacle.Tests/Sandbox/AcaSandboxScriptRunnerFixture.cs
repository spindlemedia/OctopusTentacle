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
        [TestCase("if [[ ! -x \"$CalamariExecutablePath\" ]]; then\n    chmod +x \"$CalamariExecutablePath\"\nfi\nsetsid  \"$CalamariExecutablePath\" find-and-register-package -packageId \"TaxOffice.DB\" -packageVersion \"2026.16.0-ci.7\" -packageVersionFormat \"Semver\" -packageHash \"0e8f12401806ed218d38df34238ed6729575cdca\" -exactMatch \"True\" -taskId \"ServerTasks-826567\" -variables \"Variables.secret\" -variables \"Variables.Bash.secret\" -variablesPassword=$variablePassword &\n", true)]
        [TestCase("setsid  \"$CalamariExecutablePath\" run-script -script \"Script.sh\" &\n", false)]
        [TestCase("echo ' find-package '\n", false)]
        [TestCase("\"$CalamariExecutablePath\" find-package\n\"$CalamariExecutablePath\" run-script\n", false)]
        public void OnlyABootstrapWhoseOneCalamariCallManagesThePackageCacheRunsLocally(string bootstrap, bool local)
        {
            AcaSandboxScriptRunner.IsPackageCacheCommand(bootstrap).Should().Be(local);
        }

        // Octopus sends this before every Calamari call (ServerTasks-826586).
        const string CapabilityProbe = "#!/bin/bash\nencode_servicemessagevalue ()\n{\n    echo -n \"$1\" | openssl enc -base64 -A\n}\nos=$( encode_servicemessagevalue $(uname -s) )\narch=$( encode_servicemessagevalue $(uname -m) )\nif [ -x \"$(command -v setsid)\" ]; then\n    setsid_available=$( encode_servicemessagevalue \"true\" )\nelse\n    setsid_available=$( encode_servicemessagevalue \"false\" )\nfi\necho \"##octopus[os Arch='$arch' Name='$os' capability_setsid='$setsid_available']\"\necho \"##octopus[bootstrapper Name=\\\"QmFzaA==\\\"]\"\n";

        // Shape of the coordinator's PackageRetentionJournal.json (BOM included); the package was pushed hours before this use.
        const string Package = "/etc/octopus/Files/TaxOffice.DB@S2026.16.0-ci.7@8BED873DD2BA2A4D84FAC88CA565742B.nupkg";
        const string Journal = "﻿{\"JournalEntries\":[{\"usages\":[{\"CacheAgeAtUsage\":{\"Value\":5},\"DateTime\":\"2026-10-08T01:55:04.1764234+00:00\",\"DeploymentTaskId\":\"ServerTasks-826842\"},{\"CacheAgeAtUsage\":{\"Value\":66},\"DateTime\":\"2026-10-08T10:26:00.1+00:00\",\"DeploymentTaskId\":\"ServerTasks-827366\"}],\"locks\":[],\"Package\":{\"PackageId\":{\"Value\":\"TaxOffice.DB\"},\"Version\":{\"Version\":\"2026.16.0-ci.7\",\"Format\":\"Semver\"},\"Path\":{\"Value\":\"" + Package + "\"}},\"FileSizeBytes\":4594957}],\"Cache\":{\"CacheAge\":{\"Value\":66}}}";
        static readonly DateTime Pushed = new(2026, 10, 8, 1, 55, 3, DateTimeKind.Utc);

        // ServerTasks-827366: the package-cache check runs on the coordinator, so a package Octopus pushed earlier is not
        // pushed again, and its file time stops meaning "this task uses it".
        [Test]
        public void APackagePushedEarlierButJustUsedGoesToTheSandbox()
        {
            var (recent, prune) = AcaSandboxScriptRunner.PlanPackageCache(new[] { (Package, Pushed) }, Journal, new DateTime(2026, 10, 8, 10, 26, 6, DateTimeKind.Utc), TimeSpan.FromHours(2));
            recent.Should().Equal(Package);
            prune.Should().BeEmpty();
        }

        [Test]
        public void APackageUsedInTheLastDayIsNotPruned()
        {
            var (recent, prune) = AcaSandboxScriptRunner.PlanPackageCache(new[] { (Package, Pushed.AddDays(-3)) }, Journal, new DateTime(2026, 10, 8, 20, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(2));
            recent.Should().BeEmpty();
            prune.Should().BeEmpty();
        }

        [Test]
        public void APackageUnusedForADayIsPruned()
        {
            var (recent, prune) = AcaSandboxScriptRunner.PlanPackageCache(new[] { (Package, Pushed) }, Journal, new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(2));
            recent.Should().BeEmpty();
            prune.Should().Equal(Package);
        }

        [TestCase(CapabilityProbe, true)]
        [TestCase(CapabilityProbe + "setsid  \"$CalamariExecutablePath\" run-script -script \"Script.sh\" &\n", false)]
        [TestCase("#!/bin/bash\nsqlcmd -i Update.sql\n", false)]
        public void OctopusCapabilityProbeRunsLocally(string bootstrap, bool local)
        {
            AcaSandboxScriptRunner.IsCapabilityProbe(bootstrap).Should().Be(local);
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
