# syntax=docker/dockerfile:1
# The game API as a container, for the production box (deploy/docker-compose.yml).
# Built from the repo root: the API project references MuggaLuggaTD.Shared beside it.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first, from the project files alone, so a code-only change reuses the restore layer.
COPY MuggaLuggaTD_2D.API/MuggaLuggaTD.Shared/MuggaLuggaTD.Shared.csproj MuggaLuggaTD_2D.API/MuggaLuggaTD.Shared/
COPY MuggaLuggaTD_2D.API/MuggaLuggaTD_2D.API/MuggaLuggaTD_2D.API.csproj MuggaLuggaTD_2D.API/MuggaLuggaTD_2D.API/
RUN dotnet restore MuggaLuggaTD_2D.API/MuggaLuggaTD_2D.API/MuggaLuggaTD_2D.API.csproj

COPY MuggaLuggaTD_2D.API/MuggaLuggaTD.Shared/ MuggaLuggaTD_2D.API/MuggaLuggaTD.Shared/
COPY MuggaLuggaTD_2D.API/MuggaLuggaTD_2D.API/ MuggaLuggaTD_2D.API/MuggaLuggaTD_2D.API/
# The Shared project's copy-into-Unity step is skipped here: it only runs when the Unity repo sits
# beside this one, which it never does in a container.
RUN dotnet publish MuggaLuggaTD_2D.API/MuggaLuggaTD_2D.API/MuggaLuggaTD_2D.API.csproj \
        -c Release -o /app/publish /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# wget is what the compose healthcheck calls; the aspnet image ships neither wget nor curl.
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080
USER app
# Extra arguments become commands: `migrate`, `invite-codes --count 5` (see Program.cs).
ENTRYPOINT ["dotnet", "MuggaLuggaTD_2D.API.dll"]
