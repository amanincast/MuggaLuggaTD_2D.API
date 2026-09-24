namespace MuggaLuggaTD.Shared.World
{
    /// <summary>
    /// The character of a region: what it looks like, and what it is worth holding.
    /// Values are persisted in the world blob — do not renumber.
    ///
    /// <para>These name a <b>kind of land</b>, and they are the one biome vocabulary: terrain, the
    /// Tavern's favoured affinity, and the art folders a biome's scenery and characters are filed
    /// under all key on them. They were once RiverVale, Thornwood, Fenland and Marsh, which read as
    /// the names of places rather than kinds of place — those survive as region names in the client's
    /// dossier, where a place name belongs. Renaming a member is free (the blob stores the number);
    /// renumbering one re-biomes every stored region.</para>
    ///
    /// <para>Lakeland is freshwater — inland lakes are what the generator grows. A saltwater coast
    /// would be its own biome, with water anchored to an edge of a region on the rim of the map.</para>
    /// </summary>
    public enum BiomeType
    {
        Grassland = 0,
        Forest = 1,
        Lakeland = 2,
        Highland = 3,
        Volcanic = 4,
        Swamp = 5,

        /// <summary>Open sand, mesas and the odd oasis — and the ruins of whatever was here first.</summary>
        Desert = 6
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
