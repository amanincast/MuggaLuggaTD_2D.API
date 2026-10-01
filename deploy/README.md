# Deploying MuggaLugga (production box)

The API, its Postgres and the public site run as one compose project on the Club.Manager VPS
(`74.208.203.85`, IONOS, Ubuntu), as the `deploy` user. TLS is not here: the box's front door is
**`she-bee-extras/league-hub-edge`**, which routes over the external Docker network `edge`:

| Host | Container alias | What |
|---|---|---|
| `api-muggalugga.she-bee-solutions.com` | `muggalugga-api:8080` | REST + SignalR (`/hubs/game`) |
| `muggalugga.she-bee-solutions.com` | `muggalugga-web:8080` | download page (`www/`) + launcher files (`/downloads/`) |

Its routing file is `league-hub-edge/config/sites/muggalugga.caddy` in she-bee-extras.

## On the server

Checkout: `~/muggalugga-api` (read-only deploy key, ssh alias `github-muggalugga-api`).
Secrets: `~/muggalugga-api/deploy/.env` (gitignored, server only; see `.env.example`).

```bash
cd ~/muggalugga-api && git pull
cd deploy
docker compose --env-file .env build api
docker compose --env-file .env run --rm api migrate     # startup never migrates by itself
docker compose --env-file .env up -d
docker compose --env-file .env ps                       # api should read (healthy)
```

**Invite codes** (registration needs one):
```bash
docker compose --env-file .env run --rm api invite-codes --count 5 --note "Sam"
docker compose --env-file .env run --rm api invite-codes --list
docker compose --env-file .env run --rm api invite-codes --revoke ABCD-EFGH
```

**Turning away old game builds:** set `CLIENT_MINIMUM_VERSION` in `.env` (builds are `0.1.<commit
count>`), then `up -d`. Builds older than it are told to open the launcher.

## What lives where
- `pgdata` volume: the database. **Back it up before a migration that changes data**:
  `docker compose --env-file .env exec db pg_dump -U muggalugga muggaluggaTD > ~/backups/muggalugga-$(date +%F).sql`
- `DOWNLOADS_DIR` (default `deploy/downloads/`, gitignored): published game builds, written by the
  publish tool, served read-only at `/downloads/`.
- Memory is capped (api 1 GB, db 768 MB, web 128 MB) because the box is shared with Club.Manager.

## First-time setup (done 2026-09-30)
1. `docker network create edge` (already exists: league-hub-edge made it).
2. Deploy key: `ssh-keygen -t ed25519 -f ~/.ssh/github_muggalugga_api`, added read-only to the repo, with a
   `Host github-muggalugga-api` alias in `~/.ssh/config`; `git clone git@github-muggalugga-api:amanincast/MuggaLuggaTD_2D.API.git ~/muggalugga-api`.
3. `.env` from `.env.example` with `openssl rand` secrets.
4. The commands above, then add `muggalugga.caddy` to league-hub-edge and reload it.
