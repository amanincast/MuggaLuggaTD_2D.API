# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

ASP.NET Core 8.0 REST API for the MuggaLuggaTD_2D game. Provides shared data management endpoints for the game client.

## Build and Run Commands

```bash
# Build the project
dotnet build MuggaLuggaTD_2D.API/MuggaLuggaTD_2D.API/MuggaLuggaTD_2D.API.csproj

# Run in development mode (opens Swagger UI at /swagger)
dotnet run --project MuggaLuggaTD_2D.API/MuggaLuggaTD_2D.API/MuggaLuggaTD_2D.API.csproj

# Run with specific profile
dotnet run --project MuggaLuggaTD_2D.API/MuggaLuggaTD_2D.API/MuggaLuggaTD_2D.API.csproj --launch-profile https

# Run the tests
dotnet test MuggaLuggaTD_2D.API/MuggaLuggaTD_2D.API.Tests/MuggaLuggaTD_2D.API.Tests.csproj

# Build a ready-to-play test realm, then exit (Development only; stop the running API first)
dotnet run --project MuggaLuggaTD_2D.API/MuggaLuggaTD_2D.API/MuggaLuggaTD_2D.API.csproj --launch-profile https -- seed-playtest --user <username or email> [--name Playtest]
```

**The playtest seed rebuilds, it never repairs** (`PlaytestSeeder`). It deletes the realm of that
name the user owns and makes it again: world provisioned and the player seated through the normal
services, the seven starters at level 10, four hires with real `HiredCharacter` records (Rare and
Epic Clerics, a Legendary Mage, a Common Warrior — one of each rarity, so every awakening stage is
on hand), every material ×200 (crystals ×3) and 20,000 gold. The save is written directly, so
`PlaytestSeederTests` pins that `ReconcileRoster` leaves it untouched — otherwise the client's first
save would silently strip the seeded rolls.

## Tests

`MuggaLuggaTD_2D.API.Tests` (xUnit, net8.0) covers the rules the server owns and no client may be
trusted with. It runs against EF's in-memory provider and needs neither Postgres nor a running API.

- **Through the services' public entry points**, not their privates. `WorldPveService` is exercised
  via `BeginAsync`/`ClaimAsync`, because both bugs that service has had were in what the rule should
  be, and a test poking `ValidatePveTarget` would have agreed with the bug.
- **Worlds are built with the real generator** (`TestSupport/TestWorld.cs`), never hand-written. A
  site id exists only because `RegionGenerator` produced it, so a fabricated blob would describe a
  world the server would never accept a claim against.
- **Tests state what a player should be able to do**, not what the code currently does.
  `Hold_WithNoGarrison_IsZero` passed for weeks while asserting an exploit was correct.
- `IGameContentProvider` is faked (`TestSupport/FakeGameContent.cs`) so a test can define an
  ability's upgrade pool without editing shipped content.
- PvP's dice come from `Random.Shared` and cannot be seeded, so those tests either check the result
  is consistent with the roll it reports, or rig the power gap and repeat until the wanted outcome
  comes up.

## Contesting a rival region

`WorldRaidService` + `RaidController` (`POST /api/gameinstance/{id}/raid`). It **replaced**
`WorldPvPService`, which had been dead since the world became regions — it resolved its target
against a flat top-level `Locations` array that format-4 worlds do not have, so every attack was
refused as "location not found". Two-client multiplayer had never been played, so nothing caught it.

The replacement is not a port, because the old rule cannot survive the move. It handed the holding to
whoever won one d20; against regions that is one roll taking a region, which `docs/design/siege.md`
rules out in a sentence: the server cannot referee real-time combat, so **no single fight may be
worth a region**.

So a raid **takes nothing**. It wears the region's resolve down by a bounded 5–15 (`RaidResolver`,
shared), resolve multiplies hold, and a worn-down region is cheaper to besiege later. What guards it
is not the dice but the **cooldown** — one raid per attacker per region per 4h, recorded in
`RegionRaid` and charged win or lose, so a forged win buys one cooldown's worth of progress.

- Capitals cannot be raided at all: a seat cannot be besieged, so wearing it down leads nowhere.
- The defender's answer is `RegionResolveRules`: clearing a hostile site inside a region you hold
  restores resolve, applied in `WorldPveService.ClaimAsync`.
- **Site rotation** (`SiteRotationRules`, shared 1.39.0; Mike 2026-10-03: players must never sit
  waiting for respawns). A clear is **per player** (`PlayerSiteClear`, one row per player per site),
  never written into the shared world, so nobody else's region empties.
  - **Lockout:** the clearer may not begin that site again for **10 minutes** (`PveError.SiteLocked`,
    409). Every clear pays full experience, gold, gear and materials: the rotation is the farming loop.
  - **Realm rewards** come once per player per site every **8 hours** (`WorldRewardsAt`): the resolve
    restore (so a defender restores one site's worth per site per 8h, the rate the old respawn was
    tuned for), the Tavern recruit and its refresh reset, the Hiring Hall refresh reset, and the
    season points (`PveController`, gated on `WorldRewards`). First Steps' clear counts every time.
  - **Open-field sites pay from planned waves** (`WavePlan`, 1.42.0; Mike 2026-10-04): 3-10x the
    enemies, each at a share, so a run pays 1.75x the old one. `ClaimAsync` and auto-fight pass
    `site:` to both calculators; dungeons, ruins, keeps and ambushes are priced as before.
  - **No taper** on repeats; it would punish the rotation. It is the lever if farming outruns the economy.
  - `GET pve/clears` returns the player's clears still locked or cooling. A season reset deletes them.
  - `SiteOverride.Cleared`/`ClearedAtUtcTicks` are legacy (pre-1.39.0 worlds); nothing writes them.
- A region's garrison sum, supply and hold come from `RegionHoldCalculator.AssessRegion` — one
  implementation, so the server judges a raid by the numbers the client's dossier showed the player.
- **Sieges are not built.** The gate is displayed and nothing acts on it; it is blocked behind the
  win condition (design §8).

## NPC factions (`docs/design/npc-factions.md`, Unity repo)

`FactionService` + `FactionController` (`GET api/gameinstance/{id}/factions`), one `FactionState` row per
faction per realm, and `FactionRaid` rows for what they do.

- **Strength is settled lazily** by `FactionStrengthRules` (shared 1.43.0). Its cap is what the faction's
  land supports (`HoldFloor(tier) × EntrenchmentMultiplier` per region), read from the world on every
  settle, so a faction that loses land is at once no stronger than what is left. It refills over 24h,
  at half that while Bloodied (8h).
- A row is made on first read **at full strength**; a season reset deletes the rows (`ResetRealmAsync`).
- **A ransom paid for heroes a faction holds is banked as its strength** (`WorldGarrisonService.RansomAsync`;
  Mike, 2026-10-06), up to its cap.
- **They raid** (phase 2, shared 1.44.0, `FactionDecisionRules`).
  - `SiegeScheduler` calls `ActAllAsync` every 15 minutes. A ready (60%+), unbloodied faction acts by chance
    and temperament.
  - It raids a **bordering** player region (never a seat or land under truce) or the other faction's, with
    30% of its strength, through `RaidResolver`, taking resolve only.
  - It costs a tenth of the march, or half and Bloodied (8h) if repelled. The 4h cooldown per region
    applies.
  - The war log line uses `actorUserId = "faction:Grimjaw"` and the faction's name
    (`WarLogService.RecordAsync(actorName:, subjectName:)`). The world is persisted and broadcast.
- **They lay sieges** (phase 3, shared 1.45.0, `FactionSiegeRules`, `FactionService.Sieges.cs`, `FactionSiege` rows).
  - A siege lean declares on a bordering player region at resolve 50 or below whose gate 60% of strength clears;
    with nothing ripe it raids instead. One siege per region across players and factions
    (`WorldSiegeService.DeclareAsync` asks `IsBesiegedByFactionAsync`); a mustering faction does nothing else.
  - `SettleDueSiegesAsync` settles at muster close (minute sweep in `SiegeScheduler`, and on reads): a hold past the
    gate turns it away, else `PassivePvPResolver`. Falling: `WorldRegionBlob.CaptureWreckedForFaction`, garrison
    captured, a tenth of the march lost. Failing: the whole march lost, Bloodied, resolve +15 to the region.
  - Break the siege: `POST siege/{id}/sortie/begin|claim` in `SiegeController` (defender only, once, during the
    muster; a sortie in the field holds settlement until claimed or its 2h grace ends). Won: broken (`SiegeBroken`
    in the war log). Lost: the party is Bloodied.
  - `WorldSiegeService.LiveSiegesAsync` appends them as `SiegeResponse` (`AttackerFaction`, `Broken`, `SortieBegun`),
    and they broadcast as `SiegeUpdated`, so the client treats them as sieges.
- **They grow and fight each other** (phase 4, shared 1.46.0, `FactionGrowthRules`).
  - **Expand:** wild land on the border, tier 3+, touching no capital (`WorldRegionBlob.ClaimForFaction`, no truce),
    for 20% of strength; war log `Expanded`. With none left, a lean to expand fortifies instead.
  - **Fortify:** the most threatened own region (frontier, then most worn, then least walled), walls +1 and
    resolve +15 at once, for 15% of strength; war log `Fortified` with the faction as actor. It is how a
    faction's land recovers from raids, since nobody clears its sites.
  - **Faction against faction:** a faction besieges the other's worn land as a player's. The `FactionSiege`
    row's `DefenderUserId` is then "faction:{name}" (`DefenderFaction` parses it; no migration), nobody may
    sally out, and a Bloodied defender holds at a quarter less.
  - `FactionBalanceSimulation` (tests) runs a week of turns on three generated worlds against absent players
    and prints the report; `FACTION_SIM_DAYS=28` for a season.
- **Temperament is server-only content:** `GameContent/Server/FactionData.json` (`IGameContentProvider.Factions`).
  It is deliberately not in `DocumentNames`: the client fails its content sync closed on a document it
  does not know.
- `POST factions/debug` drives the Unity Combat Debug window: set strength, Bloody, clear, `ForceAct` (with `Action`
  "Siege", "Expand" or "Fortify"; a raid otherwise), `CloseMuster`, and
  `SimulateHours`, which replays the factions' turns through those hours. It returns 404 unless the API
  runs in **Development**.
- **They race the players on the scoreboard** (season end, shared 1.47.0): see the next section.

## Quests (`QuestService`, `QuestController`; `docs/design/quests.md` in the Unity repo)

- **The board is never stored.** `QuestRules.Board(realm, user, hour, set, world, peoples, tuning)` works it
  out: up to 4 village offers (villages, `NeutralHome`, in lit regions not held by a rival), up to 3 wandering givers
  (`r12:npc0`/`npc1`, 2 slots a region, each with a calling: Hunter→Slay, Pilgrim→Clear, Pedlar→Gather, Scout→an ambush
  in that region; `QuestOffer.Calling`, 1.49.0) and 2 Hall offers.
  Each village is rolled on its own seed, so new land in sight can displace an offer but never rewrite one.
- **What is stored:** `QuestBoardState` (per player per realm) holds the set number and, for the current hour,
  the offers taken and the givers seen (the client's gold "?"). A new hour empties both lists. `PlayerQuest`
  holds a taken offer frozen as JSON, with its progress. Both are wiped by the season reset.
- **A fresh set at once:** once every offer on the board has a handed-in row, `Set` is raised.
- **Deeds** go through `QuestService.RecordAsync` (idempotent, never throws), called from the PvE claim (a
  fightable site's clear plus kills), the ambush claim (a win plus kills) and auto mode (at its share).
- **Kills are client-reported and clamped.** `PveClaimRequest.Kills` and `AmbushClaimRequest.Kills` give a
  tally by people. `QuestRules.ClampKills` keeps only the fight's biome's peoples (`IGameContentProvider.EnemyPeoples`,
  from CharacterData via `Naming.Race`), up to 1.5 times `RunRewardCalculator.PlannedEnemies`.
- **Hand-in:** a Gather quest spends its goods from the wallet (workers are settled first). Others need their
  count. Pays `QuestRules.RollChest` (one piece of the tier, the rest lower), gold and materials, marked
  handed in before the grant.
- **Debug (Development only):** `POST quests/debug/fresh` and `quests/debug/finish`.

## Realm goal of the day (`RealmGoalService`, `RealmGoalController`; Unity `docs/systems/quests.md`)

- **One goal per realm per UTC day**: a `RealmGoal` row (unique realm+day), made lazily by `TodayAsync`
  from `RealmGoalRules.For` (shared; seeded by realm and day; Slay a people of the realm's biomes, Clear
  sites or win Ambushes; target scales with players who saved in the last 7 days, at least 2).
- **Fed by `QuestService.RecordAsync`**, which calls `RealmGoalService.RecordAsync` first, so every deed source
  counts (PvE claim, ambush claim, auto mode). Never throws. Each player's count is a `RealmGoalShare`.
- **Paid once, by the write that reaches it.** Goal and shares are `IRevisioned` and updated under
  `Concurrency.RetryAsync`/`RefreshAsync`. Each share of at least 2% gets a Magic chest, or Rare at 10%,
  rolled at the roster's average level and granted through the ledger. It also gets a `RealmGoalReached`
  letter (dedup `goal:{day}`). The war log notes each quarter and the end. Session log: `REALM-GOAL`.
- **Details carry parts, the client writes the words** (plurals): war log `"{pct}:{kind}:{subject}:{target}"`,
  letter `"{chest}:{mine}:{kind}:{subject}:{target}"`.
- `GET gameinstance/{id}/realm-goal` (the strip: count, ends at, my share and chest, top 5).
  Debug (Development only): `POST realm-goal/debug/fill?fraction=`.

## Letters, the inbox (`LetterService`, `LettersController`; Unity repo `Specifications` "Inbox")

Personal letters, one realm at a time: what happened to *this player*, each with an action. The war log
stays the realm's news. Mike 2026-10-08: the Hall only, no auto-open, no cross-realm view.

- **One letter per event per player:** `DedupKey` is unique per (user, realm), so a settle that runs
  twice writes nothing twice. `OccurredAt` is the event's time, not the settle's.
- **Raids and sieges come from the war log.** `WarLogService.RecordAsync` hands each line to
  `FromWarLogAsync`: the defender gets RaidOnYou / RaidRepelled / SiegeDeclaredOnYou / SiegeResultOnYou,
  the besieger YourSiegeResult, a captor PrisonersRansomed. A new path that logs gets letters for free;
  a faction (`faction:` ids) never gets one. Siege results carry `"won|detail"` for the client to word.
- **The other hooks**, each beside the code that makes the fact:
  - `PartyService.SettleArrival` notes arrivals and ambushes (dated at `ArrivesAt` / the strike), sent to the
    company's owner by whichever read settled it. The static `CompanyAtAsync` settle sends none.
  - `AutoFightService.SettleAsync`: one AutoReport per company per settle, `"name|fights|won|gold|items"`.
  - `QuestService.RecordAsync`: QuestReady when a deed finishes a quest, `"Kind|Target|Count"`.
  - `SeasonScoreService.CloseSeasonAsync`: SeasonEnded to every ranked player, `"season|rank|chest"`.
  - A won siege's `"N champions taken prisoner"` line also writes HeroesCaptured (`CapturedIn`).
- **The catch-up** (`LettersController`, before every read): the season, due sieges, the player's companies
  (`PartyService.ListAsync`, which settles auto mode too), then `LetterService.CatchUpAsync`, which writes
  HeroesReturned at capture + 8h unless the player ransomed them first.
- **Raids on one region fold within the hour** (`LetterRules.GroupWindow`): `Count` rises and the letter
  is unread again.
- **Kept 30 days, at most 200** per player per realm (`LetterRules.ToDrop`, on write). Not wiped by a
  season reset.
- **The ⚑ is worked out on read**, never stored (`FlagsAsync`): a siege letter is flagged while a live
  siege (player or faction) on that region has this player as defender; heroes while someone is held in that
  region; an ambush while the company stands halted; a quest until handed in; a season until its chest is opened.
- **Pushed to the player** with `Clients.User(userId)` ("LetterAdded"), which the default user id
  provider maps from the NameIdentifier claim.
- Routes: `GET letters?before=&take=`, `GET letters/summary`, `POST letters/read {ids | all}`. Reads
  advance due sieges first.

## The season's end (`docs/design/season-end.md`, Unity repo)

- **Factions score their land** at the players' rates (`SeasonEndRules.RateForFaction`), nothing for deeds. One
  `FactionSeasonScore` row per faction per season, settled in `SeasonScoreService.SettleAllAsync` beside the
  players. They appear in the standings and the final table as `faction:Grimjaw` with the faction's name.
- **A faction moving land settles the board then and there:** `FactionService.ActAsync` and
  `SettleDueSiegesAsync` call `SettleAllAsync` after a world change. Before this, a player away from the game
  kept earning points and gold for a region a faction had taken until they next opened the world.
- **The close ranks players and factions in one table.** A faction first means nobody is crowned. Each
  player's `SeasonResult` gets `ChestRarity` from the season's average points an hour
  (`SeasonEndRules.ChestFor`, bands calibrated by `FactionBalanceSimulation`); none for a player who scored nothing.
- **Crowns are derived**, never stored: a player's rank-1 results in tables where somebody else was ranked too.
  `Crowns` rides on standings and result entries.
- **Endpoints** (`SeasonController`): `GET season/ended` (the latest unseen closed season, 204 if none; it
  also closes an expired season), `GET season/ended/{n}`, `POST season/results/{n}/seen`,
  `POST season/results/{n}/chest` (rolls with `FirstStepsRules.RollPiece` and grants through the item ledger,
  marked opened first), and `POST season/debug/bell` (Development only: the season is made to have run its
  length, then closed as a real bell closes it).
- The `SeasonEnd` migration marks every earlier result as seen, so old seasons do not pop up as pages.

## Companies (phase 1 of `docs/design/parties-and-travel.md`, Unity repo)

`PartyService` + `PartyController` (`api/gameinstance/{id}/parties`: GET, POST, PUT/DELETE
`{partyId}`), stored as `PlayerParty` rows (per player per realm). Companies are the player's own
business, so nothing here is broadcast.

- **How many: roster cap ÷ 4** (`CompanyRules.MaxCompanies`, shared), at most 4 characters each. The
  cap can fall; like the roster, a company is **never** taken away. A player over the limit just
  cannot form another.
- **The first company is the party the player already had.** The first read forms "The Vanguard"
  from the save's `ActiveCharacterIds`, standing at the capital's keep.
- **Commitments win over membership.** `PartyService.CommitmentsAsync` is the single answer to "is
  this character free": garrisoned, held (`WorldRegionBlob.CollectCommitments`) or locked into a
  siege army. A committed character cannot join a company, and stationing a garrison **takes its
  members out of their company** (`ReleaseAsync`, from `WorldGarrisonService.SetAsync`). Mike's call:
  a company can be what's assigned to a garrison's defence, but an idle company defends nothing.
- **PvE begin names its fighters** (`PveBeginRequest.CharacterIds`, 1.32.0) and refuses any who are
  not the player's own or are committed (`PveError.FightersUnavailable`). Before this a run asked
  nothing, so a siege army could slip off and run dungeons. Who fought is kept in
  `PveRun.FighterIdsJson`. An empty list is refused.
- **Travel** (1.33.0, phase 2): `POST parties/{id}/travel { siteId }`. The route is found on the
  region's roads — `RegionRoadNetwork` (shared), the very network the client paints — and timed by
  `TravelRules` (road 5 s a cell, land 11, woods 18; the whole journey scaled into **1–5 minutes**,
  Mike's call). The company is stamped `Travelling` with `DepartedAt`/`ArrivesAt` and the route
  (cells + cumulative seconds, `RouteJson`); **nothing ticks** — the first read after `ArrivesAt`
  lands it (`SettleArrival`). The response carries `ServerNow` so the client walks it by server time.
- **Between regions** (1.35.0, phase 4): `TravelRules.PlanRoute` finds the regions by BFS over the
  world's hexes (a gap in the map is `NoRoute`) and plans each region's **leg** gate to gate, each
  clamped to 1–5 minutes, with `CrossingSeconds` (**20**) at each border. `RouteJson` holds the
  legs (`RouteLeg`: region, cells, **absolute** seconds after departure); a pre-1.35 route reads
  as empty. `RegionId` is settled per leg on every read (`RegionAlong`), so a marching company
  is in the region its road has reached. Every region has **a road toward each neighbouring hex**
  on the facing side (`RegionRoadNetwork.For`: E/W edges, NE/NW top, SE/SW bottom, each on its
  half), so a leg enters by the gate facing where it came from.
- **Other players' companies** (phase 5): `GET parties/others` (`PartyService.OthersAsync`) settles
  every company in the realm (arrivals and ambushes are fixed by the clock, so whose read it is does
  not matter), then returns the others' companies in regions the viewer can see — `RegionSight.Lit`
  (held + one ring, the map's rule) plus wherever the viewer's companies are. A `RivalCompanyDto`
  carries owner, name, banner, state, place, each member's sheet from the owner's save, and a
  journey trimmed to the legs in sight (ends nulled when out of sight); never the ambush. Every
  successful company change (form, set, travel, flee, claim, disband) broadcasts **`PartyMoved`**
  with only the mover's id: clients re-ask, so the fog lives in the GET, not in the broadcast.
- **You fight where you stand:** PvE begin takes `PartyId`; the company must be at rest **at that
  site**, and its members are the fighters (`PveError.NoCompanyThere` otherwise). A company on
  the road can be renamed but not re-manned.
- **Ambushes** (phase 3, shared 1.34.0, `AmbushRules`): rolled **once, when the travel order is
  accepted** — per leg by that land's tier, whether the player holds it (×1.75 if not) and the
  leg's length, combined as 1 − Π(1 − c) (`ChanceForRoute`), capped at 35% — and stored as `AmbushAt` (a share of the journey's time, 0.25–0.75).
  **It is never sent to the client**; the first read after that moment halts the company
  (`Ambushed`, `HaltedAt`), and only then does the DTO carry `Journey.HaltedAt` and an
  `AmbushDto` (tier 1, 3 waves; the destination's level if halted in its region, else that
  region's average site level, fought at its keep's site id so the arena takes that land's biome).
  - `POST parties/{id}/ambush/fight` opens a `PveRun` (`LocationId = "ambush:{partyId}"`,
    type -1, so a PvE claim naming it finds no site and closes it) and stores `AmbushRunId`.
  - `POST .../ambush/claim { runId, won }`: **won** (≥ `MinimumRunDuration`) pays **half** of a
    tier-1 run at that level (XP, gold from that XP, each item and material unit at a coin-flip) and
    the company **marches on** — its departure and arrival are shifted by the halt, and it cannot be
    ambushed twice on one road. **Lost** pays nothing and turns it back. No conquest, recruit,
    resolve or season points either way.
  - `POST .../ambush/flee`: **Returning** — the cells it walked, reversed and timed as they took
    (`AmbushRules.RouteBack`: every walked leg reversed, crossings included). Arrival lands it
    at `ToSiteId` (the origin) in that site's region.
  - A halted company **waits**: there is no auto-resolve for a company its player steers (Mike
    2026-09-27). `PartyService.Dice` is the ambush entropy; tests fix it.

## Auto mode (`AutoFightService`; `docs/design/auto-fight.md` in the Unity repo, phase 2)
- **A company toggled into auto mode fights on its own** (Mike 2026-10-04):
  - `POST parties/{id}/auto {on}`. It must be at rest and have members.
  - `POST parties/{id}/auto/order {order, regionId}`: Roam or Patrol, **only a region its player
    holds**. A patrol is refused (`TooStrong`) unless the region's mobs are below the company's level.
  - Both answer with the companies, and broadcast `PartyMoved`.
- **Nothing ticks.** `SettleAsync` replays each auto company from `AutoSettledAt` to now:
  - **What it replays:** walk (an ordinary journey, so the client draws it), fight, roll, pay, pick the
    next site (`AutoFightRules.NextSite`).
  - **When it stops:** caught up, out of provisions, nothing below its level, region lost, or nobody
    free.
  - **Reruns are safe:** each fight is rolled from `AutoFightCount` (`AutoFightRules.RollWin`, seeded),
    so a rerun rolls nothing twice.
  - **Bounded:** at most 48h is replayed.
  - **One settle at a time:** the Hall reads companies and reports together, and two replays of one
    stretch once paid it twice. On Postgres a transaction takes `FOR UPDATE` on the player's auto
    companies; `AutoSettledAt` is also a concurrency token. The companies are saved **first and
    alone**, so a losing settle writes no report, wound or spent provision.
  - **Called from:** `PartyService.ListAsync`, before every auto order, and the reports read.
- **Paid:**
  - **Gold and materials** go straight to the purse and wallet.
  - **Experience and gear** belong to the save, which only the client writes. They are banked in
    `AutoFightReport`, and the gear goes into the item ledger at once.
  - **The client collects the bank:** `GET parties/auto/reports`, then it applies the experience
    (to `FighterIds`) and the items, saves, and calls `POST parties/auto/reports/collect`.
- **The rules:**
  - **Pay:** a third of a clear, gear a rarity down (`RunRewardCalculator` `rarityStepsDown`).
  - **What it never does:** pay realm rewards, start a lockout, or count for First Steps.
  - **Provisions** (`ProvisionRules`): spent from `PlayerMaterials` as each fight or 20-minute patrol
    stint begins. Out of Grain, it **stops** (`OutOfProvisions`) until its player re-orders it.
  - **A patrol stint** ends in a skirmish at the region's mob level, at an ambush's share of a third.
- **Bloodied** (`BloodiedCharacter`, per character):
  - **Cause** (`AutoFightService.BloodyAsync`, 30 min): a lost auto-fight; a lost ambush claim
    (`ClaimAmbushAsync`); a run given up (`WorldPveService.AbandonAsync`, `POST pve/abandon`); a raid
    thrown back; a repelled siege (`RepelAsync`, the army). **Not** a ransom (Mike), nor a run lost
    by falling (never reported).
  - **What it does** (1.41.0; Mike: barred only where nobody steers):
    - Auto mode bars them, and so does a garrison muster (`onAGarrison`).
    - `WhyCannotFightAsync` no longer refuses them: the client fights them at 25% less.
    - A raid or siege muster counts their share at 25% less (`BloodiedRules.Weaken`).
  - **The company** rests where it stands, then resumes by itself.
  - **The client** sees `PartiesResponse.Bloodied`.
- **While in auto mode its player cannot steer it:** travel, member changes and disband are refused
  (`Busy`), and `CompanyAtAsync` will not open a hand run with it.
  - **Toggled off mid-walk:** it lands as an ordinary journey.
  - **Toggled off mid-fight:** the fight is dropped.
  - `SettleArrival` skips auto companies.
- **Season reset:** every company comes out of auto mode, and wounds heal. Uncollected reports stay.
- **A patrol halves the ambush chance** on its player's journeys through its region (`TravelAsync`
  passes it to `AmbushRules.ChanceForRoute`), and the client's risk preview reads the same.
  - **Which patrols count** (everywhere: roads, auto roads, diggings): `AutoFightRules.Guards`,
    a company under patrol orders that has not stopped. Out of provisions, it guards nothing.
- **An auto company's road is rolled too** (phase 4), in `StartWalk`, by the same chance with
  patrols counted. The roll is `AutoFightRules.RollAmbush`, seeded from the company and its
  departure, so it is the same on every rerun.
  - **The strike:** the walk's step ends where the ambush strikes (`AmbushAt` set,
    `AutoStepEndsAt` < `ArrivesAt`). `Ambushed` fights a skirmish at the mob level of the region
    struck in, and writes a report with `Ambush` set.
  - **Won:** it is paid as a patrol's skirmish and walks on.
  - **Lost:** its fighters are Bloodied, and it walks back the way it came (`PartyService.TurnBack`,
    `AutoStatus.FallingBack`). It then rests out its wounds and carries on.
  - **Toggled off mid-walk:** an ambush already rolled stays, and halts the company to be asked,
    as for any steered company.

## The equipment ledger (`ItemLedgerService`, `ItemGrant`)

Equipment lives in the client's save, and until 2026-09-28 every item there was taken at face
value. Now **the save decides who wears an item; the ledger decides what the item is and whether it
exists.**
- Every item the server rolls is recorded as an `ItemGrant` (the `ItemSaveData` as granted, less
  `EquippedByCharacterId`): PvE claims (`WorldPveService`) and won ambushes (`PartyService`).
- Every save is reconciled on `POST playerdata/me` after materials are stripped: a granted item is
  rewritten to its grant (a save cannot raise a stat or rarity), an item with no grant is dropped, a
  duplicated id keeps one copy. Compared in one form (read as `ItemSaveData`, written by
  System.Text.Json), because the client writes Newtonsoft and 12.0 must equal 12.
- **Adoption**: the first time the ledger meets a player it adopts what their **stored** save holds
  (never the arriving one, which could launder anything) and writes an `ItemLedgerState` so it
  happens once. A claim adopts before it grants, so a first claim after deploy loses nothing.
- **Bazaar**: a listing sells the ledger's copy of an item the seller holds (the request names it
  by `Id` only); while listed it is out of the seller's inventory; a sale moves the grant to the
  buyer **and into the buyer's realm** (a fresh id if that realm already uses it), a pull-back
  returns it. See *The Crossroads Bazaar* below.
- **The save's inventory is `InventoryItems`** (top level, `UserSaveData`). `StripMaterials` read
  `ItemInventory.Items` until this change — a shape no client writes — so it never stripped anything;
  its tests had been built in the same wrong shape. `ItemLedgerService.SaveItems` reads both.
- Not covered: **experience/level** is still client-written (bounded by `ClampLevels` only).

## The Crossroads Bazaar (`BazaarService`, `BazaarController`, design 12d)

A market **open to every realm** at prices **nobody sets**. Mike's calls, 2026-10-01.
- **The Assay** (`BazaarAssay`, shared, so the Sell tab quotes what the server pays): equipment is
  power² / 50 (`PartyPowerCalculator.CalculateItemPower`), materials 20 / 80 / 300 by tier × 1 essence,
  1.5 crystal, 3 shard. Tidied to 5s/10s/50s/100s, floor 10. **No price is stored**: it is worked out
  when needed, so a queue never holds two prices. The design's supply-driven price is left out on purpose.
- **The house keeps a tenth** (`BazaarAssay.Fee`), taken from the seller's side.
- **Gold crosses worlds, never moves within one**: the buyer pays from the realm in the route; the
  seller is paid into the realm the listing came from (`MarketplaceListing.GameInstanceId`), and
  collects it there (`POST .../bazaar/collect`). `GoldService.CreditAsync` pays it — not a clear.
- **A material is one queue**, oldest listing first across every seller and realm. A purchase is all
  or nothing; a buyer is never sold their own goods; a quoted price that no longer matches is refused.
- **Goods leave the seller when listed**: equipment into the ledger's escrow, materials out of the
  wallet. Pulling back returns what is unsold; earned gold stays to collect.
- **Trades are serialised** by one static lock (one API process).
- **A season reset destroys that realm's unsold goods** (Mike: "destroyed for now", to revisit) and
  pays out what they already earned (`BazaarService.ExpireRealmAsync`, from `CloseSeasonAsync`).
- Pinned by `BazaarTests` and `SeasonScoreServiceTests.AResetTakesTheWorldsGoodsOffTheBazaar`.

## Resource sites and goods (`ResourceNodeRules`, Hiring Hall phase 1)

- **Every ResourceNode has a trade, and so a good:**
  - Worked out by `ResourceNodeRules.TradeOf(siteId, biome)`, never stored. Every biome can turn up
    every trade.
  - Goods are `MaterialCategory.Goods` in MaterialData (Ore, Timber, Grain, Stone, Hides),
    undroppable.
  - Workers will gather them, and fortifying and siege supplies will spend them (plan in the Unity
    repo, `docs/design/hiring-hall.md`).
- **Bazaar price:** one goods unit is `BazaarAssay.GoodsUnitPrice` (2 g), deliberately under the
  floor every other tag starts at.
- **Naming:** a node is named with its trade's words (`Naming.NodeWords(trade)`).

## The Hiring Hall (`HiringService`, `HiringController`, design 12e phase 2)

- **Tables:** `HiringCandidates` (the six benches), `HiredWorkers`, `HiringStates` (refresh count,
  arrival clock), per player per realm. Routes under `api/gameinstance/{id}/hiring`: GET, `hire`,
  `refresh`, `assign` (a null site sends the worker home); each answers with the whole room.
- **Rolls and rates are shared** (`HiringRules`): tier, traits, cost, base rate, `RateAt` for a
  worker at a site, `Gathered` (Lucky hours by worker id and hour index), beds (2 a region held).
- **Board:** a first visit fills six; then one arrival every 2 h into a free bench (not banked
  when full). Refresh 100 g, doubling until a dungeon/portal clear (`WorldPveService` calls
  `ResetRefreshAsync`).
- **Hire:** a bed, then the gold (`GoldService.SpendAsync`); the bench is freed.
- **Assign** checks the site is a ResourceNode in a region the player holds, of a trade they can
  work, with a free place. It settles the player first, then re-rates both sites (Foreman).
- **Settling is lazy and rides the season settle**: `SeasonScoreService.SettleAllAsync` calls
  `HiringService.SettleAllAsync` after gold, so a region changing hands pays its workers and sends
  them home in the same pass. The wallet GET and the room GET settle that one player first. Whole
  goods go to the material wallet (`hiring-output`); the fraction stays in `Carry`.
- **Raiders at the diggings** (auto-fight.md §7, phase 5): in an hour `HarassmentRules.IsHarried`
  picks (about 1 in 10, from realm, region and hour; nothing stored), an unpatrolled region's
  workers gather at **half** pace (`HiringRules.Gathered` with a harried-hour function).
  - **A patrol keeps them off:** `GuardedAsync` reads the player's guarding patrols as they stand
    *now*. A settle over a long stretch therefore credits the patrol for all of it, or none. Every
    wallet or company read settles both, so while the player plays the stretch is short.
  - The client shows it from the same rule (`HiringHandler.IsHarriedNow`); the server sends nothing.
- **Season reset** (`HiringService.ResetRealmAsync`): workers, benches, states and every good in
  the realm's wallets are removed.
- Pinned by `HiringRulesTests` and `HiringServiceTests`.

## Fortify (`FortifyService`, `FortifyController`, Hiring Hall phase 3)

- `POST api/gameinstance/{id}/fortify` `{regionId, sharedContractVersion}`.
  - Checks the region (`FortifyRules.Check`: yours, below V, no works under way, no siege
    mustering or assaulting).
  - Settles the player's workers, then spends the bill from the material wallet
    (`FortifyRules.CostFor`: Stone + Timber 150 × level, Ore 100 × (level − 3) from IV, × tier
    factor 1 + 0.5(tier − 1)).
  - Marks the blob (`FortifyingTo`, `FortifyEndsAtUtcTicks`, via `WorldRegionBlob.SetFortifying`)
    and broadcasts.
- **The works row** (`RegionFortifications`): from/to, spent, started, completes, state
  (UnderWay/Done/Cancelled).
- **Finishing:** `SiegeScheduler`'s sweep calls `CompleteAllDueAsync`, and the world GET calls
  `CompleteDueAsync` first.
  - The realm is settled at `CompletesAt` with the old walls, entrenchment is raised and the
    scaffolding cleared, then it is settled again at that instant to re-rate.
  - War log `Fortified` (detail: the new level's numeral).
  - **"Still ours" is owner + target level, not the end tick**, so a mismatch can never strand
    scaffolding that would block the region for good.
  - Works on land that changed hands are Cancelled; `CaptureRegion` clears the scaffolding.
- Season reset deletes the rows. Pinned by `FortifyTests`.

## Siege supplies (`SiegeSupplyRules`, Hiring Hall phase 4)

- `WorldSiegeService.DeclareAsync` charges goods **after every other check**, so a refused declare
  costs nothing: `SiegeSupplyRules.CostFor(assessment.Hold)`.
  - Per 1,000 hold: Grain 300, Timber 200, Hides 150, Ore 100, each rounded up to a ten.
  - Hold is floored at 500.
- It settles the attacker's workers first (`HiringService.SettlePlayerAsync`, optional in tests),
  then spends from the material wallet. Short: `SiegeError.CannotSupply` → 409 `{message}`, and no
  siege is made.
- Spent whatever the siege comes to; logged in `SIEGE-DECLARE` as `supplies=`.
- `WorldSiegeService` now needs `MaterialWalletService`; the tests' seed stocks the attackers
  (`supplied: false` to start empty).
- Shared contract 1.38.0.

## First Steps and the Rare chest (`FirstStepsService`, `FirstStepsController`, design 12c)

- **Six steps per player per world, and one Rare chest for doing them all** (Mike, 2026-10-01): every
  world has its own, so a veteran on a new world earns the same chest a newcomer does. A season reset
  deletes the realm's progress (`ResetRealmAsync`), so each new map has its chest too.
- **The chest grants an item, so the server records the steps that could earn it, where they
  happen**, after the action has saved:
  - a hire (`TavernService.HireAsync`)
  - a march (`PartyService.TravelAsync`)
  - a Dungeon/Portal claim (`WorldPveService`, the same test that brings a recruit)
  - a garrison with somebody in it (`WorldGarrisonService.SetAsync`)
- **What a client may report:** only `company` and `equip`, which grant nothing on their own
  (`FirstStepsRules.MayClientReport`; `POST first-steps/mark` refuses the rest).
- **Recording never fails the action it records**: a lost race is caught and logged. The services
  take `FirstStepsService` as an **optional last constructor argument**, so tests that build them by
  hand need not.
- **The chest:** `POST first-steps/open-chest`. All six must be done and it opens once. It rolls one
  equipment piece at level 5, **always Rare** (`ItemDropCalculator.ApplyDropProperties` takes an
  optional rarity), and grants it through the item ledger, so the next save keeps it.
- Pinned by `FirstStepsTests` and a hook test in each of the Tavern, PvE and Party suites.

## Run picks and Trainer talents (`PlayerSaveValidator.StripRunPicks`, `TalentService`)

- **A save carries no ability upgrades.** A run's level-up picks end with the run (Mike,
  2026-09-29), so `StripRunPicks` drops every applied upgrade a save holds. It used to keep those
  found in the content pool.
- **Trainer talents** (design 6c) live on each saved character as `Talents` (rank by node id,
  `TalentRules` in the shared assembly), and **only `TalentService` writes them**:
  - `POST .../talents/learn` adds one rank, checked with `TalentRules.WhyNot` against the stored level.
  - `POST .../talents/respec` clears them all for `RespecCost` gold, in one write with the spend.
  - Both edit the stored `PlayerGameData` blob in place.
  - Every `POST playerdata/me` puts each character's talents back to what the stored save held
    (`ReconcileTalents`, run on the pre-merge blob). So power pricing reads them straight from the
    roster, and no table or migration was needed.
- **Power:** 25 a point (`PartyPowerCalculator`). Hold the Line multiplies a character on a
  garrison (`MarchingArmy.MusterAsync(..., onAGarrison: true)` from `WorldGarrisonService`).
- Points come from the saved level, which is client-written and only clamped: the same gap as
  awakening.

## Tester access: invite codes and the version gate

- **Registration needs an invite code** while `Registration:RequireInviteCode` is on. It is on by default
  and off in `appsettings.Development.json`, so local registration still works. A code is **spent
  before the account is made and refunded if creation fails**, so a failed registration does not burn a
  single-use code. `ApplicationUser.InviteCode` records which code made the account.
- **Codes are minted from the command line**, in any environment. There is no admin endpoint to attack:
  `dotnet run -- invite-codes --count 5 --uses 1 --days 30 --note "Sam"`, then `--list`, then
  `--revoke CODE`. On the server, run the same through `docker compose exec`.
  - Codes look like `XXXX-XXXX` and leave out 0/O/1/I/L/U.
  - Case, spaces and the dash are forgiven.
- **`ClientVersionGate`** turns away a game build older than `Client:MinimumVersion` (e.g. `0.1.300`).
  - The game sends `X-Client-Version`. A too-old build gets **426** with an `AuthResponse`-shaped body, so
    the login screen shows "out of date, open the launcher" as-is.
  - A request **without** the header passes. This is for honest stale builds, not security;
    `SharedContract.Version` still guards the rules.
  - An unparsable version is never "older".
  - `GET /api/client/version?current=` reports the minimum and whether an invite code is needed.
  - Raise the minimum when a release must not be mixed with older builds.
- Pinned by `TesterAccessTests`.

## Production (deploy/)

- The API runs on the **Club.Manager VPS** (74.208.203.85) as the compose project `muggalugga`: api,
  Postgres, and a small Caddy `web` for the download page and launcher files. Full steps are in `deploy/README.md`.
- **TLS is the box's shared front door**, `she-bee-extras/league-hub-edge`. It reaches `muggalugga-api` and
  `muggalugga-web` on the external `edge` network; nothing publishes a host port.
- **Migrations are a deploy step** (`docker compose run --rm api migrate`). Startup never migrates.
- **Secrets** (`JWT_KEY`, `POSTGRES_PASSWORD`) live only in the server's `deploy/.env`. They override the
  development values committed in `appsettings.json`, which must never be used in production.
- The API trusts `X-Forwarded-*` (it sits behind Caddy) and serves `/healthz` for the container healthcheck.
- Memory is capped because the box is shared with Club.Manager.

## Development URLs

- HTTP: http://localhost:5081
- HTTPS: https://localhost:7212
- Swagger UI: http://localhost:5081/swagger (development only)

## Architecture

- **Minimal hosting model**: Configuration in `Program.cs` (no Startup.cs)
- **Controllers**: Attribute-routed REST controllers in `Controllers/` directory
- **DI pattern**: Services registered in `Program.cs`, injected via constructor
- **Swagger/OpenAPI**: Enabled in development for API documentation

## Project Structure

```
MuggaLuggaTD_2D.API/
└── MuggaLuggaTD_2D.API/
    └── MuggaLuggaTD_2D.API/   # Main API project
        ├── Controllers/       # API endpoints
        ├── Properties/        # Launch settings
        └── Program.cs         # Entry point and DI config
```

## Key Configuration

- **Nullable reference types**: Enabled
- **Implicit usings**: Enabled
- **.http file**: REST client test file available for endpoint testing
