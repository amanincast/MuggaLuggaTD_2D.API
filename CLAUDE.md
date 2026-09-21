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
```

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
- A region's garrison sum, supply and hold come from `RegionHoldCalculator.AssessRegion` — one
  implementation, so the server judges a raid by the numbers the client's dossier showed the player.
- **Sieges are not built.** The gate is displayed and nothing acts on it; it is blocked behind the
  win condition (design §8).

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
