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

**Known gap, recorded in the suite:** `WorldPvPService` still resolves its target against a flat
top-level `Locations` array, which no current (format 4) world has — so every PvP attack on a real
site is refused as "location not found". `WorldPvPServiceTests.AnAttackInARegionWorld_CannotFindItsTarget`
pins that, and the skipped `ARivalsKeepInARegionWorld_CanBeBesieged` beside it states the requirement
for whoever ports PvP to regions.

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
