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
- `pgdata` volume: the database.
  - **Backed up nightly** by `backup.sh` (deploy's crontab, 02:30 UTC, after Club.Manager's at 02:00).
    Dumps go to `~/backups/muggalugga/`, kept 14 days, and the job logs to `~/muggalugga-backup.log`.
    The restore steps are at the top of the script.
  - **Run it by hand before a migration that changes data:** `./backup.sh`.
  - The dumps live on the same box. An off-box copy is not set up yet.
- `DOWNLOADS_DIR` (default `deploy/downloads/`, gitignored): published game builds, written by the
  publish tool, served read-only at `/downloads/`. It holds `MuggaLuggaSetup.exe` (the launcher, which
  the download page links), `blobs/`, and one folder per channel with its `manifest.json`.
- `www/`: the download page. It reads the version and patch notes from the testers manifest, so a publish
  needs no page edit. A page change goes live on `git pull`, since `www/` is mounted, with no restart.
  A `web/Caddyfile` change needs `docker compose --env-file .env restart web`.
- Memory is capped (api 1 GB, db 768 MB, web 128 MB) because the box is shared with Club.Manager.

## First-time setup (done 2026-09-30)
1. `docker network create edge` (already exists: league-hub-edge made it).
2. Deploy key: `ssh-keygen -t ed25519 -f ~/.ssh/github_muggalugga_api`, added read-only to the repo, with a
   `Host github-muggalugga-api` alias in `~/.ssh/config`; `git clone git@github-muggalugga-api:amanincast/MuggaLuggaTD_2D.API.git ~/muggalugga-api`.
3. `.env` from `.env.example` with `openssl rand` secrets.
4. The commands above, then add `muggalugga.caddy` to league-hub-edge and reload it.
