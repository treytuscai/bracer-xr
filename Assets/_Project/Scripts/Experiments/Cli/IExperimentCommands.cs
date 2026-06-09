using System;
using System.Collections.Generic;

namespace Experiments.Cli
{
    /// <summary>
    /// Implemented by any scene controller that wants to expose commands to the
    /// <see cref="ExperimentCommandServer"/> CLI. The server rescans the active scene on
    /// every load and calls <see cref="RegisterCommands"/> on each provider it finds, so a
    /// controller declares its own commands without the server ever referencing its type.
    /// This is the seam that lets app logic (e.g. grid placement) sit on top of the surface
    /// without coupling the transport to any one experiment.
    /// </summary>
    public interface IExperimentCommands
    {
        /// <summary>
        /// Add this controller's commands to <paramref name="commands"/>. The key is the verb
        /// typed on the CLI (e.g. "clear"); the handler receives parsed key=value args and
        /// returns a one-line reply. Handlers run on the Unity main thread.
        /// </summary>
        void RegisterCommands(IDictionary<string, Func<IReadOnlyDictionary<string, string>, string>> commands);
    }
}
