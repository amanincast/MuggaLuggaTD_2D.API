namespace MuggaLuggaTD.Shared.World
{
    /// <summary>
    /// The character of a region: what it looks like, and what it is worth holding.
    /// Values are persisted in the world blob — do not renumber.
    /// </summary>
    public enum BiomeType
    {
        RiverVale = 0,
        Thornwood = 1,
        Fenland = 2,
        Highland = 3,
        Volcanic = 4,
        Marsh = 5
    }

    /// <summary>
    /// What a cell of a region's interior is made of, before any art is chosen.
    ///
    /// Deliberately coarse. The shared generator decides ground so that client and server agree on
    /// where a site may legitimately stand; the client alone decides which tile to paint, because
    /// the server has no business knowing about sprites.
    /// Values are derived from a seed rather than persisted, so they may be renumbered freely.
    /// </summary>
    public enum TerrainClass
    {
        Land = 0,
        Water = 1,
        Forest = 2,
        Mountain = 3
    }

    /// <summary>
    /// Who holds a region. Players are identified by user id on the region itself; this names the
    /// non-player powers, which the design treats as static flavour owners for now and as real
    /// threats later.
    /// Values are persisted in the world blob — do not renumber.
    /// </summary>
    public enum FactionId
    {
        None = 0,
        Player = 1,
        Ashkin = 2,
        Grimjaw = 3
    }
}
