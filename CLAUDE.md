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
  restores resolve, applied in `WorldPveService.ClaimAsync`. A cleared site **recovers after 8h**
  (`SiteRespawnRules`), because otherwise that answer is finite while raiding is not and the attacker
  wins by arithmetic. Ask `SiteRespawnRules.IsCleared`, never `SiteOverride.Cleared` — clearance is
  time-dependent, and `WorldRegionBlob.MarkCleared` stamps `ClearedAtUtcTicks` for it.
- A region's garrison sum, supply and hold come from `RegionHoldCalculator.AssessRegion` — one
  implementation, so the server judges a raid by the numbers the client's dossier showed the player.
- **Sieges are not built.** The gate is displayed and nothing acts on it; it is blocked behind the
  win condition (design §8).

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
  - A halted company **waits**: there is no auto-resolve (Mike 2026-09-27 — the design's auto-fight,
    morale and fatigue were not asked for). `PartyService.Dice` is the ambush entropy; tests fix it.

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
- **Marketplace**: a listing sells the ledger's copy of an item the seller holds (the request's
  `ItemData` only names it by `Id`); while listed it is out of the seller's inventory; a sale moves
  the grant to the buyer, a cancel returns it. **No price is charged yet** — the marketplace's
  economy waits on its design (`PurchaseConditions` is stored, not enforced).
- **The save's inventory is `InventoryItems`** (top level, `UserSaveData`). `StripMaterials` read
  `ItemInventory.Items` until this change — a shape no client writes — so it never stripped anything;
  its tests had been built in the same wrong shape. `ItemLedgerService.SaveItems` reads both.
- Not covered: **experience/level** is still client-written (bounded by `ClampLevels` only).

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
