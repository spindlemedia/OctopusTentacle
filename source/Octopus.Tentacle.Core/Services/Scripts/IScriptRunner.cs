using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Octopus.Tentacle.Core.Services.Scripts.Logging;
using Octopus.Tentacle.Scripts;

namespace Octopus.Tentacle.Core.Services.Scripts
{
    /// <summary>
    /// Runs a prepared script workspace somewhere other than as a local child process,
    /// for example in a short-lived sandbox per script.
    /// </summary>
    public interface IScriptRunner
    {
        /// <summary>
        /// True when every script gets a machine of its own, so the per-process isolation mutex has nothing to protect.
        /// </summary>
        bool RunsEachScriptOnItsOwnMachine { get; }

        Task<int> RunAsync(
            IScriptWorkspace workspace,
            string shellPath,
            string taskId,
            IReadOnlyDictionary<string, string> environmentVariables,
            IScriptLogWriter writer,
            CancellationToken cancellationToken);
    }
}
