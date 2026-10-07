#if !NETFRAMEWORK
using System;
using FluentAssertions;
using NUnit.Framework;
using Octopus.Tentacle.Sandbox;
using Octopus.Tentacle.Startup;

namespace Octopus.Tentacle.Tests.Sandbox
{
    [TestFixture]
    [NonParallelizable]
    public class AcaSandboxShutdownFixture
    {
        [TestCase("AcaSandbox", true)]
        [TestCase(null, false)]
        public void OnlyTheSandboxCoordinatorIgnoresSigterm(string? runner, bool ignored)
        {
            var previous = Environment.GetEnvironmentVariable(AcaSandboxConfiguration.RunnerVariable);
            Environment.SetEnvironmentVariable(AcaSandboxConfiguration.RunnerVariable, runner);
            try
            {
                using var registration = OctopusProgram.IgnoreSigtermWhileTheEntrypointDrains();
                (registration != null).Should().Be(ignored);
            }
            finally
            {
                Environment.SetEnvironmentVariable(AcaSandboxConfiguration.RunnerVariable, previous);
            }
        }
    }
}
#endif
