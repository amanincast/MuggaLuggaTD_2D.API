namespace MuggaLuggaTD.Shared.World
{
    /// <summary>
    /// Types of locations that can be spawned on the world map.
    /// Each type maps to a category of prefabs with tier-based variants.
    ///
    /// Shared with the API: the server decides what conquering a location does, and validates
    /// whether a location is a legitimate PvE target, so it needs the same type/ownership vocabulary
    /// the client uses. Values are the integers persisted in the world blob — do not renumber.
    /// </summary>
    public enum LocationType
    {
        Dungeon = 0,
        Portal = 1,
        Outpost = 2,
        Castle = 3,
        NeutralHome = 4,

        // Added with the region map (design 8a), which fills a region's interior with more than
        // places to fight: a node produces resources over time, a ruin can be repaired to raise the
        // region's entrenchment. Appended deliberately — these integers are persisted.
        ResourceNode = 5,
        Ruin = 6
    }

    /// <summary>
    /// Ownership status of a spawned world location.
    /// Values are the integers persisted in the world blob — do not renumber.
    /// </summary>
    public enum LocationOwnership
    {
        Neutral = 0,
        Enemy = 1,
        Player = 2
    }
}
