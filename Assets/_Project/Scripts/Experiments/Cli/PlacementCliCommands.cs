using System;
using System.Collections.Generic;
using UnityEngine;

namespace Experiments.Cli
{
    /// <summary>
    /// Shared expctl verb for skin-mesh placement experiments.
    /// </summary>
    public static class PlacementCliCommands
    {
        public static void Register(IDictionary<string, Func<IReadOnlyDictionary<string, string>, string>> commands)
        {
            commands["placed"] = _ =>
            {
                string reply = LastWidgetPlacement.FormatReply();
                Debug.Log("[expctl placed] " + reply);
                return reply;
            };
        }
    }
}
