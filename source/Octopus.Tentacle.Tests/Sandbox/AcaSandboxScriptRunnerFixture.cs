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

        [TestCase("/a/./b/../c", "/a/c")]
        [TestCase("/../../etc", "/etc")]
        [TestCase("//a//b/", "/a/b")]
        public void NormalizesSandboxPaths(string path, string expected)
        {
            AcaSandboxScriptRunner.NormalizeUnixPath(path).Should().Be(expected);
        }

        [TestCase("https://registry.example:5000", "team/app", "1.2", "registry.example:5000/team/app:1.2")]
        [TestCase("https://registry.example.azurecr.io", "team/worker", "16-1", "registry.example.azurecr.io/team/worker:16-1")]
        [TestCase("https://index.docker.io", "ubuntu", "24.04", "docker.io/library/ubuntu:24.04")]
        [TestCase(null, "octopusdeploy/worker-tools", "6", "docker.io/octopusdeploy/worker-tools:6")]
        public void ImageNameKeepsTheRegistryPort(string? feedUri, string packageId, string version, string expected)
        {
            AcaSandboxScriptRunner.ContainerImagePull.ImageName(feedUri, packageId, version).Should().Be(expected);
        }

        [Test]
        public void QuotedEnvValuesKeepTheirSpaces()
        {
            const string dockerRun = "docker run --entrypoint='' --rm \\\n"
                + "  --env TentacleHome=$TentacleHome \\\n"
                + "  --env \"GREETING=hello there\" \\\n"
                + "  --env 'PATH_WITH_SPACE=/opt/my tools' \\\n"
                + "  image:tag \\\n";

            AcaSandboxScriptRunner.ContainerStep.ParseEnvironment(dockerRun).Should().BeEquivalentTo(new System.Collections.Generic.Dictionary<string, string>
            {
                ["TentacleHome"] = "$TentacleHome",
                ["GREETING"] = "hello there",
                ["PATH_WITH_SPACE"] = "/opt/my tools"
            });
        }

        [Test]
        public void ToolSnapshotKeyIgnoresOrderAndTrailingSlashButNotTheDiskImage()
        {
            var a = AcaSandboxToolSnapshots.Key("disk-1", new[] { "/etc/octopus/Tools/A/1", "/etc/octopus/Tools/B/2/" });
            AcaSandboxToolSnapshots.Key("disk-1", new[] { "/etc/octopus/Tools/B/2", "/etc/octopus/Tools/A/1" }).Should().Be(a);
            AcaSandboxToolSnapshots.Key("disk-2", new[] { "/etc/octopus/Tools/A/1", "/etc/octopus/Tools/B/2" }).Should().NotBe(a);
            AcaSandboxToolSnapshots.Key("disk-1", new[] { "/etc/octopus/Tools/A/1", "/etc/octopus/Tools/B/3" }).Should().NotBe(a);
        }
    }
}
#endif
