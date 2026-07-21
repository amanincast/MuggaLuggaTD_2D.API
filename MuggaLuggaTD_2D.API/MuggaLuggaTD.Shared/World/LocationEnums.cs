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
        NeutralHome = 4
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
