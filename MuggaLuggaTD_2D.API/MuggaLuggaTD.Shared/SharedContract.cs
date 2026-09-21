namespace MuggaLuggaTD.Shared
{
    /// <summary>
    /// Identifies the version of the shared gameplay rules.
    ///
    /// The Unity client consumes this assembly as a DLL committed under Assets/Plugins, so it can
    /// fall behind the server's copy. Since both sides resolve PvP with this code, a stale client
    /// would compute power under different rules than the server. The client sends this version with
    /// every server-resolved action and the API rejects a mismatch, so the failure is a clear error
    /// instead of a silent disagreement about who won.
    ///
    /// Bump this whenever a change alters gameplay results — new or changed weights, modifier
    /// behaviour, or resolution thresholds. Pure refactors and comments do not need a bump.
    /// </summary>
    public static class SharedContract
    {
        // 1.1.0 — conquest rules (ConquestResolver + location enums) moved into this assembly when
        //   PvE conquest became server-applied; a 1.0.0 client still writes conquests through the
        //   world blob and must not be allowed to.
        // 1.2.0 — PvE rewards and ability-upgrade legality moved server-side. Older clients grant
        //   their own rewards and persist unvalidated upgrades, so must re-sync before playing.
        // 1.3.0 — GameAbility no longer pre-fills the adjusted layers of Range, CollisionScale,
        //   PierceCount, ProjectileCount and ChainCount. Those defaults outranked the content values
        //   in GetCurrentValue(), so a 1.2.0 client resolves every ability's range as 5 and every
        //   projectile count as 1 whatever the data says. That changes damage output and therefore
        //   power, so the two sides must not be allowed to disagree about it.
        // 1.4.0 — the wrist and cape slots are saved and count toward power. A 1.3.0 client drops
        //   those items on save, so the two sides would price the same party differently.
        // 1.6.0 — RegionGenerator grows terrain by accretion instead of by random walk. Terrain is
        //   not stored, so the generator *is* the data: a 1.5.0 client would draw different ground
        //   and, worse, place sites on different cells, so the server would reject its claims as
        //   naming sites that do not exist. Worlds are regenerated — see WorldRegionBlob v3.
        // 1.7.0 — a region's hold has a floor. It was garrison x entrench x supply x resolve, which
        //   is zero with no garrison or with resolve ground to zero, so the siege gate was zero and
        //   an unattended region could be taken with nothing at all. Hold now adds a per-tier floor
        //   for the walls and locals, and resolve counts for no less than a quarter. A 1.6.0 client
        //   would show the player a gate the server does not agree with.
        // 1.8.0 — a region is 32x18 cells rather than 24x24, so the territory view fills a 16:9
        //   screen instead of floating in it. Same 576 cells, so nothing about the land or the site
        //   budget changes — but every cell index means a different place, so a 1.7.0 client would
        //   draw the wrong ground and put sites where the server does not have them.
        // 1.9.0 — raiding. Passive PvP resolved against a flat list of locations that region worlds
        //   no longer have, so every attack was refused as "location not found" and PvP had in fact
        //   been dead since format 4. It is replaced by the raid of docs/design/siege.md §3, which
        //   is measured against a region's hold rather than one site's garrison snapshot: a raid
        //   takes nothing and wears resolve down instead, because the server cannot referee a
        //   real-time fight and so no single fight may be worth a region. A region's garrison sum,
        //   supply and hold now have one implementation (RegionHoldCalculator.AssessRegion) rather
        //   than living only in the client's dossier, and clearing a fightable site inside your own
        //   region restores its resolve. A 1.8.0 client would show the player a raid bar and an
        //   expected cost the server does not agree with.
        public const string Version = "1.9.0";
    }
}
