using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// What should happen to a world location when the player wins the combat tied to it.
    /// </summary>
    public enum ConquestOutcome
    {
        /// <summary>No conquest effect (e.g. location type is not combat-attached).</summary>
        None = 0,

        /// <summary>Ownership transfers to the victorious player (Castle, Outpost).</summary>
        CaptureForPlayer = 1,

        /// <summary>The location is removed from the world (Portal, Dungeon).</summary>
        RemoveLocation = 2
    }

    /// <summary>
    /// Pure mapping of (location type, combat result) -> conquest outcome.
    ///
    /// Shared with the API, which applies the conquest itself rather than trusting the client's
    /// world write. Both sides must agree on what winning at a location does, so this mapping has
    /// exactly one implementation.
    ///
    ///   - Castle / Outpost  -> CaptureForPlayer (ownership flips to the winner)
    ///   - Portal / Dungeon  -> RemoveLocation   (cleared and removed)
    ///   - NeutralHome / unknown -> None
    /// </summary>
    public static class ConquestResolver
    {
        /// <summary>
        /// Resolves the outcome for a player victory at a location of the given type.
        /// </summary>
        public static ConquestOutcome ResolveOnPlayerVictory(LocationType type)
        {
            switch (type)
            {
                case LocationType.Castle:
                case LocationType.Outpost:
                    return ConquestOutcome.CaptureForPlayer;

                case LocationType.Portal:
                case LocationType.Dungeon:
                    return ConquestOutcome.RemoveLocation;

                default:
                    return ConquestOutcome.None;
            }
        }

        /// <summary>
        /// Convenience: true if winning at this location type transfers ownership to the player.
        /// </summary>
        public static bool IsCapturable(LocationType type)
            => ResolveOnPlayerVictory(type) == ConquestOutcome.CaptureForPlayer;
    }
}
