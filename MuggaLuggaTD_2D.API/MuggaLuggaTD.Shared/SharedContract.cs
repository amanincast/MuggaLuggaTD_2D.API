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
        // 1.10.0 — a cleared site recovers after a while instead of staying spent forever. Clearing
        //   a region's own hostile sites is the only way to restore its resolve, so a permanent
        //   clear made a region's defence finite while raiding it was not, and an attacker won by
        //   arithmetic however well the defence was played. Sites now carry when they were cleared
        //   and come back; both sides must agree, because the server refuses a run against a site it
        //   thinks is spent and the client would otherwise draw a marker the server will not admit.
        // 1.11.0 — seasons. A realm now runs for a length its creator sets and is won on points, which
        //   accrue per hour for ground held (weighted by tier, entrenchment and whether it is the
        //   heartland) and in lumps for clearing sites, landing raids and repelling them. The client
        //   shows a player what their holdings earn and where they stand, so both sides must price a
        //   region the same way; the server alone decides what is banked.
        // 1.12.0 - sieges, first half. A region that has just changed hands is under truce and can
        //   be neither raided nor besieged; a siege may be declared once raids have worn resolve to
        //   50, with an army reaching the gate, and never in the last day of a season. The dossier
        //   tells the player whether they may declare and why not, so it must apply the same rules
        //   the server will - and a 1.11.0 client would offer a raid the truce now refuses.
        // 1.13.0 - the siege assault. The server hands the attacker an encounter (enemy level, waves)
        //   set by how far their army clears the frozen hold; winning takes the region wrecked, losing
        //   repels it. The client builds the fight from those numbers, so both sides must compute them
        //   the same way.
        // 1.14.0 - materials became server-owned. A cleared run pays them into a wallet the server
        //   holds (MaterialRewardCalculator prices it), saves no longer carry them, and spending goes
        //   through an endpoint. They are the Tavern's currency, so a client-written balance would be
        //   a client-minted one. A 1.13.0 client keeps writing materials into its save and would
        //   believe it holds what the server has dropped.
        // 1.19.0 - player classes. A character has a Class that provides its basic ability and how
        //   fast it gains health, and player base health is 2.5x what it was (design doc 03 §2b).
        //   Health is a term in PvP power, so a 1.18.0 client and this one disagree about a party.
        // 1.18.0 - bosses. From tier 3 the last wave carries a boss (x12 health, x1.5 damage, phases
        //   at 66% and 33%) and does not end until it is dead; RunRewardCalculator prices it.
        // 1.17.0 - elites. Some of each wave from the third on is an elite (x2 health, x1.25 damage,
        //   x1.5 experience), and RunRewardCalculator prices them, so the payout matches the fight.
        //   A 1.16.0 client fights waves with no elites in them and would be paid for elites.
        // 1.16.0 - an upgrade whose Property is "Damage" now increases damage. It was mapped onto
        //   "Range" instead, so most of the upgrade content in the game silently buffed range and
        //   left damage untouched; affinity damage is also reset before upgrades are re-applied, so
        //   two damage picks no longer compound on each other.
        // 1.15.0 - the balance pass (design doc 03). Enemy health and damage scale through one shared
        //   EnemyStatScaling (the client and the reward pricing disagreed by a level), enemy damage is
        //   a 0.4 factor of the ability growing linearly rather than the ability's full value
        //   compounding at 10%, and levels cost 4000 x 1.2^(L-1) to a cap of 30 instead of
        //   100 x 1.5^(L-1) uncapped. A 1.14.0 client fights different enemies and levels at a
        //   different rate than the server prices.
        public const string Version = "1.19.0";
    }
}
