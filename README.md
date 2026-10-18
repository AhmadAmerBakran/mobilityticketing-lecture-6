# MobilityTicketing: Redis journey search

This repository contains my work for lecture 6 in Databases for Developers. I used the lecture 6 starter and kept the existing MongoDB and PostgreSQL fixtures. The C# application now checks Redis before querying MongoDB.

The cache key includes the city, both stops and the search time range. Times are normalized to UTC and the key contains a format version. Results are stored as JSON with a schema version, a cache timestamp and the journey list. A missing, expired or invalid value triggers a new MongoDB search.

The normal cache lifetime is 20 seconds. Redis is not the source of truth. If Redis is unavailable, the search still uses MongoDB. If both services are unavailable, the program reports an error rather than returning an empty journey list.

## Running the lab

Docker Desktop and .NET 8 or later are needed. Run these commands from the repository root:

```sh
docker compose up -d mongo redis
docker compose exec -T redis redis-cli PING
docker compose exec -T mongo mongosh --quiet --file /scripts/lecture05/reset.js
dotnet run --project database/redis/lecture06/CacheLab -- repeat
```

The first search should find `LAB05-T-OK` through MongoDB. The next search uses Redis. Each printed result shows its key, source, cache status, trip IDs, MongoDB call count and elapsed time.

I added named scenarios so the exercises can be repeated without editing C# between runs:

```sh
dotnet run --project database/redis/lecture06/CacheLab -- keys
dotnet run --project database/redis/lecture06/CacheLab -- collision
dotnet run --project database/redis/lecture06/CacheLab -- empty
dotnet run --project database/redis/lecture06/CacheLab -- expiry
dotnet run --project database/redis/lecture06/CacheLab -- invalid
dotnet run --project database/redis/lecture06/CacheLab -- cancel
dotnet run --project database/redis/lecture06/CacheLab -- concurrent
dotnet run --project database/redis/lecture06/CacheLab -- seats
dotnet run --project database/redis/lecture06/CacheLab -- data
dotnet run --project database/redis/lecture06/CacheLab -- verify
```

The `verify` scenario runs the connected-service checks and throws an error if an assertion fails. The cancellation scenario restores the fixture's trip status, and temporary Redis keys are removed where appropriate.

For the two outage cases, stop services separately and use the `search` scenario:

```sh
docker compose stop redis
dotnet run --project database/redis/lecture06/CacheLab -- search

docker compose stop mongo
dotnet run --project database/redis/lecture06/CacheLab -- search

docker compose start mongo redis
docker compose down
```

The first command should still return the MongoDB result; the second should exit with a search error. The Redis and MongoDB outage tests are also included in the GitHub Actions workflow.

The lab observations, explanations and cache lifetime decisions are in [the lecture 6 evidence](docs/evidence/lecture06/README.md). The [dossier](docs/dossier.md) links the relevant files.
