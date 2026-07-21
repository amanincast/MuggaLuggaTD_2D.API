using System;
using System.Collections.Generic;

namespace Abilities.Models
{
    public class AbilityActivationTracker
    {
        public double RemainingTimeUntilActivation { get; set; }

        public double RoundedRemainingTimeUntilActivation
        {
            get
            {
                return Math.Round(RemainingTimeUntilActivation, 2);
            }
        }

        /// <summary>
        /// The live instances spawned by this ability. Typed as <see cref="object"/> because this
        /// type is shared with the server, which has no UnityEngine reference — the Unity side holds
        /// UnityEngine.Object instances here and type-checks on the way out.
        /// </summary>
        public List<object> Activations { get; set; } = new List<object>();

    }
}
