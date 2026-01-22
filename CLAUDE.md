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
```

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
