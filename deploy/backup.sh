#!/usr/bin/env bash
# Nightly backup of the MuggaLugga database (a gzipped pg_dump), kept for 14 days. On the box:
#   crontab -e
#   30 2 * * * /home/deploy/muggalugga-api/deploy/backup.sh >> /home/deploy/muggalugga-backup.log 2>&1
#
# Restore (stop the api first so nothing writes meanwhile):
#   docker compose --env-file .env stop api
#   gunzip -c <file>.sql.gz | docker compose --env-file .env exec -T db psql -U muggalugga -d muggaluggaTD
#   docker compose --env-file .env start api
# into an empty database; for a clean slate drop and recreate it first (dropdb/createdb in the db container).
set -euo pipefail

cd "$(dirname "$0")"   # deploy/, where docker-compose.yml and .env live

ENV_FILE=".env"
set -a; . "./$ENV_FILE"; set +a

OUT_DIR="${BACKUP_DIR:-$HOME/backups/muggalugga}"
RETAIN_DAYS="${BACKUP_RETAIN_DAYS:-14}"
mkdir -p "$OUT_DIR"

STAMP="$(date +%Y%m%d-%H%M%S)"
FILE="$OUT_DIR/muggalugga-$STAMP.sql.gz"

# Written under a temporary name and renamed once complete, so a failed dump never looks like a backup.
docker compose --env-file "$ENV_FILE" exec -T db pg_dump -U muggalugga muggaluggaTD | gzip > "$FILE.part"
mv "$FILE.part" "$FILE"

echo "$(date -Iseconds) wrote $FILE ($(du -h "$FILE" | cut -f1))"

find "$OUT_DIR" -name 'muggalugga-*.sql.gz' -mtime +"$RETAIN_DAYS" -delete
find "$OUT_DIR" -name 'muggalugga-*.sql.gz.part' -mtime +1 -delete
