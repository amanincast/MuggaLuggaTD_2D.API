using System;

// This lived in `Environment.WorldView` in the Unity assembly, but that name cannot survive the move
// into a DLL: plugins under Assets/Plugins are auto-referenced by EVERY assembly in the project, so a
// root `Environment` namespace shadows System.Environment inside Unity's own package code (ShaderGraph
// failed to compile on `Environment.NewLine`). Everything this assembly exports at the root must
// therefore be a name no BCL or Unity namespace collides with.
namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// Outcome of a passive PvP fight (attacker party vs defender garrison).
    /// </summary>
    public struct PassivePvPResult
    {
        public bool AttackerWins;
        public int D20Roll;
        public int Modifier; // power-gap modifier applied to the roll
        public int Total;    // D20Roll + Modifier
    }

    /// <summary>
    /// Pure resolution of a passive PvP fight (see docs/design/garrison-and-passive-pvp.md).
    /// Dice + power modifier: roll a d20, add a clamped modifier derived from the power gap, and
    /// compare to a fixed threshold. At equal power the modifier is 0 so rolls 11-20 win (50%);
    /// the modifier is clamped to +/-9 so even a huge power gap leaves at least a 5% upset chance.
    ///
    /// The d20 roll is injected, which is what lets the server own it: the client no longer rolls its
    /// own dice, it just renders the result the server returns.
    /// </summary>
    public static class PassivePvPResolver
    {
        /// <summary>Power difference (attacker - defender) that shifts the roll by one point.</summary>
        public const float PowerPerModifierPoint = 100f;

        /// <summary>Largest modifier magnitude, so the strong side never has a guaranteed result.</summary>
        public const int MaxModifier = 9;

        /// <summary>Total (roll + modifier) at or above which the attacker wins. 11 => 50% at equal power.</summary>
        public const int WinThreshold = 11;

        /// <summary>
        /// Resolves the fight. <paramref name="d20Roll"/> must be in [1, 20].
        /// </summary>
        public static PassivePvPResult Resolve(float attackerPower, float defenderPower, int d20Roll)
        {
            // Math.Round's default is half-to-even, matching the UnityEngine.Mathf.RoundToInt this
            // replaced — keep it so a power gap of exactly n.5 resolves the same as it always has.
            var rawModifier = (int)Math.Round((attackerPower - defenderPower) / PowerPerModifierPoint);
            int modifier = Math.Max(-MaxModifier, Math.Min(MaxModifier, rawModifier));

            int total = d20Roll + modifier;
            return new PassivePvPResult
            {
                D20Roll = d20Roll,
                Modifier = modifier,
                Total = total,
                AttackerWins = total >= WinThreshold
            };
        }
    }
}
