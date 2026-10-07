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
        // 1.20.0 - signatures and affinities. A character is now a class, a signature and an
        //   affinity, and its kit is derived from that triple rather than listed per character: the
        //   class basic plus the signature's ability, retuned to the affinity so it deals that type
        //   and applies its status effect. Retuning preserves total damage, but the mage's Blast is a
        //   different explosion per affinity, so which ability a character casts - and therefore the
        //   damage term in its power - depends on the roll. A 1.19.0 client reads none of it and
        //   would fight with the old hand-listed abilities. Design doc 05 §3.
        // 1.21.0 - the Tavern. Characters are hired from a six-recruit board the server rolls and
        //   restocks when a dungeon is cleared, paid for out of the material wallet at a price set by
        //   rarity. The client shows the board, the odds and the cost before the player spends, so
        //   both sides must price a recruit the same way - and every hire leaves a record the save is
        //   reconciled against, so a roll a 1.20.0 client wrote for itself is now stripped.
        // 1.22.0 - awakening. A signature now grows with the character: rarity sets the ceiling
        //   (Common II, Rare III, Epic IV, Legendary Apex) and level sets the climb (II at 10, III at
        //   20, IV and Apex at 30). The stages are cumulative and are DERIVED from rarity and level
        //   rather than stored, so a save cannot claim one it has not earned - but they raise the
        //   signature's damage, which means power is no longer the same number on both sides unless
        //   both derive it. Retuning was safe to leave out of power before this because it preserves
        //   total damage; awakening is not. A 1.21.0 client would fight with an unawakened signature
        //   while the server priced an awakened one. Design doc 05 §2.
        // 1.23.0 - resonance. A party that shares a signature affinity deals more of it (+10/20/30%
        //   for 2/3/4), holds its status longer from three, and spreads it at four. ResonanceRules is
        //   shared because it is a pure function of the party's rolls and the Guild Hall shows the
        //   same answer the fight uses - but it is deliberately NOT wired into PartyPowerCalculator,
        //   because whether a garrison's composition counts for hold is left open by the design
        //   (doc 05 §3). So this version changes no server-computed result: it is bumped to keep the
        //   client and the DLL it ships with honest about each other, not because the two sides could
        //   disagree. Reactions are the other half and are client-only, keyed on a combat status the
        //   server never sees.
        // 1.24.0 - gold. A currency the player never picks up: a claimed clear pays one figure
        //   derived from the experience that clear is worth (GoldRules.GoldPerExperience, computed in
        //   the same walk so the two cannot drift), and ground held pays by the hour on the same
        //   weighting the season prices land at - with its own base rate, because points are the win
        //   condition and gold is the economy, and retuning one must not silently retune the other.
        //   This DOES change a server-computed result: RunRewards now carries Gold, so a client on an
        //   older DLL would be claiming against a payout it cannot describe.
        // 1.25.0 - Tavern lures and pity. A crystal offered before a run pulls the board that comes
        //   back from it toward one affinity (25/40/60% of slots for Minor/Major/Perfect), and every
        //   lured board that misses adds 10% to the next. Expressed as a TARGET SHARE rather than a
        //   weight multiplier, because the affinity roll is weighted over the affinities a signature
        //   is allowed - rarely all eight - so only a share means the same thing for every signature.
        //   The unlured roll path is untouched, so an existing seeded board rolls exactly as before.
        // 1.26.0 - the Tavern board became seats rather than a batch. A cleared dungeon now ADDS one
        //   recruit to a free seat instead of rolling six new faces over the old six, because farming
        //   the materials to afford a recruit used to be the very thing that took that recruit away -
        //   saving up was self-defeating. Hiring frees a seat, a full board takes nobody, and a paid
        //   refresh (RefreshCostGold) is the only thing that removes a recruit the player did not
        //   hire. RestocksTheBoard is renamed BringsARecruit to say what it now means.
        // 1.27.0 - the Tavern refresh escalates. Each paid refresh costs double the last
        //   (TavernRules.RefreshCostFor), and a claimed dungeon puts it back to the base price. A flat
        //   price was no gate for a rich player: enough gold bought enough rolls to fish for one exact
        //   class/signature/affinity, which made lures pointless - there is no reason to spend a
        //   crystal shifting odds you can simply buy more dice against. The reset is dungeon-gated
        //   rather than timed, because the escalation is meant as a pull back toward playing.
        // 1.30.0 - the Desert biome (BiomeType 6). New worlds roll it away from the start; its
        //   regions are mostly open sand, carry a portal and a ruin from tier 2, and favour Arcane in
        //   the Tavern. Stored worlds are untouched: no existing region changes biome or terrain.
        // 1.31.0 - the Cleric, and support abilities. An ability can carry Healing (with who it lands
        //   on), a heal-target count, a support radius and a ward, all cloned from the template and
        //   reachable by upgrades. PartyPowerCalculator prices healing 1:1 with damage, so a Cleric's
        //   garrison, raid and siege strength include what it mends; a 1.30.0 client would price one
        //   on its damage alone.
        // 1.32.0 - companies (docs/design/parties-and-travel.md). CompanyRules: a company is four, a
        //   player may form roster cap / 4. PvE begin names the characters who fight, and the server
        //   refuses one that is garrisoned, held prisoner or locked into a siege - it checked none of
        //   those before, so a sieging army could slip off and run dungeons.
        // 1.33.0 - travel (parties-and-travel.md §3). The road network moved into the shared assembly
        //   (RegionRoadNetwork) so the server times a journey on the road the client paints; TravelRules
        //   times it (1-5 minutes). PvE begin names a company, which must be standing at the site.
        // 1.34.0 - ambushes (parties-and-travel.md §4). AmbushRules: the server rolls a journey when it
        //   sets out; a company that reaches its ambush halts until its player fights (a tier-1 skirmish
        //   at the land's level, paying half) or flees (walking back the way it came).
        // 1.35.0 - travel between regions (parties-and-travel.md §6, phase 4). Every region has a road out
        //   toward each hex neighbour, on the side facing it; a journey is planned region by region
        //   (TravelRules.PlanRoute) and stored as legs, each walked on its own roads with a 20s crossing
        //   between. Ambush odds combine over the legs; fleeing walks every leg back.
        // 1.36.0 - the Trainer (design 6c): permanent talents, a point a level plus one at 10, 20 and 30,
        //   spent on one stat tree for every class (TalentRules). A character's talents are priced at 25
        //   power a point, and Hold the Line raises what it is worth on a garrison.
        // 1.37.0 - the Crossroads Bazaar (design 12d). BazaarAssay prices every item from its power and
        //   every material from its tier, and the house keeps a tenth; the Sell tab quotes the seller what
        //   the server will pay, so the two must agree.
        // 1.38.0 - goods spent on war (Hiring Hall phases 3-4). Fortifying spends stone, timber and ore
        //   for a level of entrenchment (FortifyRules), and declaring a siege spends grain, timber, hides
        //   and ore scaled by the target's hold (SiegeSupplyRules); the dossier and the codex quote both.
        // 1.39.0 - site rotation (SiteRotationRules). A clear is per player, not written into the shared
        //   world: it locks that player out of the site for ten minutes, and its realm rewards (resolve,
        //   a recruit, refresh resets, season points) come once per player per site every eight hours.
        //   SiteRespawnRules is gone.
        // 1.40.0 - auto-fight (docs/design/auto-fight.md, Unity repo). AutoFightRules: a side company
        //   fights only sites below its average level, wins by the gap, pays a third and gear a rarity
        //   down; BloodiedRules bar a loser from every fight for 30 minutes; ProvisionRules spend Grain
        //   and Hides; a patrolled region halves the ambush chance.
        // 1.41.0 - Bloodied in fights the player chose (Mike, 2026-10-04). A Bloodied hero may fight by
        //   hand at a quarter less (BloodiedRules.Penalty) and marches on a raid or siege at a quarter of
        //   their power less (BloodiedRules.Weaken); auto mode and garrisons still bar them. A lost
        //   ambush, an abandoned run, a lost raid and a repelled siege now Bloody too; a ransom does not.
        // 1.42.0 - Planned waves in the open field (WavePlan; Mike, 2026-10-04). Each wave is a roster
        //   that grows by wave and tier, the next comes on a clock or when the field is clear, and the
        //   run ends on an empty field. Open-field sites are paid from that roster at a share per enemy
        //   (1.75x the old run in all); placed fights and ambushes are paid as before. Bosses have
        //   3x the health (BossRules.HealthMultiplier 12 -> 36), so a boss's experience rises with it.
        // 1.43.0 - NPC factions have strength (docs/design/npc-factions.md phase 1; Mike, 2026-10-06).
        //   FactionStrengthRules: a faction's cap is what its land supports (hold floor x entrenchment per
        //   region), it refills over 24h (half while Bloodied for 8h), and a ransom paid for heroes a
        //   faction holds is banked as its strength. Nothing acts on it yet.
        // 1.44.0 - NPC factions raid (npc-factions.md phase 2). FactionDecisionRules: every 15 minutes a
        //   ready, unbloodied faction may act, by chance and its temperament; its raid marches 30% of its
        //   strength on a bordering region (a player's, never a seat or land under truce, or another
        //   faction's) through RaidResolver, takes resolve only, and costs a tenth of the march, or half
        //   and Bloodied if repelled. A Bloodied faction's land holds at a quarter less.
        // 1.45.0 - NPC factions lay sieges (npc-factions.md phase 3). FactionSiegeRules: a faction may
        //   besiege a bordering player region worn to resolve 50 or below, marching 60% of its strength,
        //   when that clears the gate. The 8h muster is settled by the server at its close: a hold raised
        //   past the gate turns it away, otherwise PassivePvPResolver decides. Falling, the region goes
        //   to the faction wrecked, its garrison captured, and the faction loses a tenth of its march;
        //   failing, it loses all of it and is Bloodied. The defender may BREAK THE SIEGE once: a sortie
        //   fought as a siege assault turned round (SortieEncounter). A mustering faction does not act.
        public const string Version = "1.45.0";
    }
}
