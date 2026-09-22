using System;
using System.Collections.Generic;

namespace MuggaLuggaTD.Shared.World
{
    /// <summary>
    /// A region of the world map: the thing a player owns and contests (design 7d).
    ///
    /// <para>The split that matters: <b>regions are owned; sites are cleared, claimed, garrisoned or
    /// repaired</b>. A siege takes a region. Running a dungeon or claiming a node changes a site
    /// inside one.</para>
    ///
    /// <para>What a region does <i>not</i> hold is its own contents. Sites come from
    /// <see cref="RegionGenerator"/> applied to <see cref="Seed"/>, so the world blob stores around
    /// eighty small rows instead of six hundred locations in a document that every garrison change
    /// rewrites. Only what diverges from the seed is stored, as overrides keyed by site id.</para>
    /// </summary>
    [Serializable]
    public class WorldRegionData
    {
        /// <summary>Stable identity, also the prefix of every site id inside it ("r12:3").</summary>
        public string RegionId;

        public HexCoord Hex;

        /// <summary>Drives the interior. Fixed at world generation and never changed afterwards.</summary>
        public int Seed;

        public BiomeType Biome;

        /// <summary>Tier drives how rich and how dangerous the interior is.</summary>
        public int Tier = 1;

        public LocationOwnership Ownership = LocationOwnership.Neutral;

        /// <summary>The API user id of the owning player. Empty for NPC factions and unclaimed land.</summary>
        public string OwnerUserId;

        /// <summary>Cached for labels, so the map can name a rival without fetching their profile.</summary>
        public string OwnerDisplayName;

        /// <summary>Which power holds it. <see cref="FactionId.Player"/> when a player does.</summary>
        public FactionId Faction = FactionId.None;

        /// <summary>True for the owner's seat of power — the region their supply lines run back to.</summary>
        public bool IsCapital;

        /// <summary>
        /// Fortification level, 0 to 5, shown as "ENTRENCH IV". Raised by repairing the region's
        /// ruins. Multiplies hold — see <see cref="RegionHoldCalculator"/>.
        /// </summary>
        public int Entrenchment;

        /// <summary>
        /// Morale, 0 to 100. Worn down by raids, restored by clearing the region's own dungeons.
        /// Scales hold directly.
        /// </summary>
        public int Resolve = 100;

        /// <summary>
        /// When the region last changed hands, as UTC ticks. Zero when it never has. Drives the truce
        /// that stops a region ping-ponging between players - ask <see cref="Gameplay.SiegeRules.IsUnderTruce"/>
        /// rather than reading this directly.
        /// </summary>
        public long ClaimedAtUtcTicks;

        /// <summary>
        /// Ids of the sites in this region whose state has diverged from what the seed generates —
        /// a dungeon cleared, a node claimed, a keep garrisoned. Sites absent from this map are
        /// exactly as generated.
        /// </summary>
        public Dictionary<string, SiteOverride> SiteOverrides = new Dictionary<string, SiteOverride>();

        public bool IsOwnedByPlayer(string userId)
        {
            return Ownership == LocationOwnership.Player
                   && !string.IsNullOrEmpty(userId)
                   && string.Equals(OwnerUserId, userId, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// What has happened to one site since the world was generated. Only the fields that changed
    /// are meaningful; the rest of the site still comes from the seed.
    /// </summary>
    [Serializable]
    public class SiteOverride
    {
        /// <summary>A dungeon or portal that has been cleared, and no longer offers a fight.</summary>
        public bool Cleared;

        /// <summary>
        /// When it was cleared, as UTC ticks. Zero when unknown.
        ///
        /// <para>A cleared site comes back — see <see cref="Gameplay.SiteRespawnRules"/>. Without
        /// this it never did, which quietly made a region's defence finite: the only way to restore
        /// a region's resolve is to clear its own hostile sites, so a defender had a ceiling that
        /// raiding did not.</para>
        /// </summary>
        public long ClearedAtUtcTicks;

        /// <summary>A ruin that has been rebuilt, and is now contributing its entrenchment.</summary>
        public bool Repaired;

        /// <summary>Characters stationed here to defend the region.</summary>
        public List<string> GarrisonCharacterIds = new List<string>();

        /// <summary>Power snapshot of that garrison, so an attacker need not load the defender's roster.</summary>
        public float GarrisonPower;

        /// <summary>Characters imprisoned here after their owner lost the region.</summary>
        public List<string> CapturedCharacterIds = new List<string>();

        /// <summary>Resources accrued and not yet collected. Empty until the economy lands.</summary>
        public int StoredYield;

        /// <summary>When the stored yield was last collected, as UTC ticks. Zero when never.</summary>
        public long LastCollectedUtcTicks;

        /// <summary>True when nothing here diverges from the generated site any more.</summary>
        public bool IsEmpty()
        {
            return !Cleared
                   && ClearedAtUtcTicks == 0
                   && !Repaired
                   && GarrisonPower == 0f
                   && StoredYield == 0
                   && LastCollectedUtcTicks == 0
                   && (GarrisonCharacterIds == null || GarrisonCharacterIds.Count == 0)
                   && (CapturedCharacterIds == null || CapturedCharacterIds.Count == 0);
        }
    }
}
