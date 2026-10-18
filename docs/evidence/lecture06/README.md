# Lecture 6: Redis journey cache

I used the `journey_search` collection from lecture 5. My test request is for city CPH, from STOP-NORREPORT to STOP-AIRPORT, on 2 October 2026 between 06:00 and 07:00 UTC. The fixture should return LAB05-T-OK. The same query between 18:00 and 19:00 UTC should return an empty list.

## Redis and the starting query

I start MongoDB and Redis with `docker compose up -d mongo redis`, check Redis with `redis-cli PING` and reset MongoDB using `database/mongodb/lecture05/reset.js`. The Redis image has persistence disabled because the cache is disposable.

For the initial command-line exercise I can use:

```sh
docker compose exec -T redis redis-cli
SET stop:LAB06:STOP-NORREPORT "Norreport" EX 20
GET stop:LAB06:STOP-NORREPORT
TTL stop:LAB06:STOP-NORREPORT
DEL stop:LAB06:STOP-NORREPORT
GET stop:LAB06:STOP-NORREPORT
```

Before expiry, GET returns the value and TTL counts down. After expiry, GET returns nil and TTL returns -2. DEL deletes only that test key, not the entire cache.

## The cache key

I first tried the incomplete key `search:LAB06:CPH:STOP-NORREPORT:STOP-AIRPORT`. It has no time range. Saving the morning result against this key means an evening search can receive LAB05-T-OK even though the corresponding MongoDB query returns nothing. The `collision` scenario reproduces this problem and removes the test key afterward.

The corrected key is built by `CacheKey.ForSearch`. It starts with `search:LAB06:v2` and includes the city, origin, destination, start and end. IDs are escaped, and start and end are converted to UTC with an invariant format. Changing either stop or the time range changes the key; reversing the stops does too. Equivalent timestamps in a different time zone generate the same key. IDs remain case-sensitive. Empty IDs and end times at or before the start are rejected.

The `keys` scenario checks these cases.

## Cached journey search

I used a cache-aside flow. The program reads Redis first. If the value is absent, expired or invalid, it asks MongoDB and writes a new cache entry. The entry contains `schemaVersion: 2`, a UTC `cachedAtUtc` timestamp and the `items` array. It is written with a 20-second expiry in the same SET operation. Reads do not extend the expiry.

The JSON must deserialize correctly, use schema version 2, contain a valid UTC timestamp and include a journey array. An invalid value is treated as a miss, not as an empty result. Redis connection errors do not stop the MongoDB lookup. A failed MongoDB lookup without a usable Redis result is reported as an error.

The command output records the key, whether the answer came from MongoDB or Redis, the cache status, trip IDs, the MongoDB call count and elapsed milliseconds. These are measured when the scenario runs, rather than filled in as assumed timings.

| Experiment | How to reproduce | Result to check |
| --- | --- | --- |
| Repeated search | `repeat` | First MongoDB, then Redis; one MongoDB call |
| Empty result | `empty` | Both return []; the second is a Redis hit |
| Expiry | `expiry` | With a one-second TTL, the next search after waiting calls MongoDB |
| Invalid JSON | `invalid` | Text, wrong version and missing timestamp are rejected and replaced |
| Same search in parallel | `concurrent` | Ten searches return the same trip; subsequent search is cached |

The `verify` command runs these tests as assertions. GitHub Actions also runs the checks using real MongoDB and Redis containers. The CI log provides the measured timings and actual MongoDB call count; these values depend on the machine and are not fixed here.

## Cancelling a departure

The cancellation experiment caches LAB05-T-OK with a five-minute TTL, then changes its status to Cancelled in MongoDB. The uncached query now returns []. Until the cache key is deleted or expires, the cached search can still show LAB05-T-OK. After deleting the exact key, the next search queries MongoDB and returns [].

Before the cancellation, a passenger sees a scheduled departure. After the database update but before invalidation, the passenger can still see the old departure. After invalidation, the result is empty. The `cancel` scenario demonstrates all three states and restores the test fixture.

If MongoDB has not received the cancellation, deleting Redis alone is insufficient: the next miss can load the old trip and cache it again. There is also a race when a search starts before a cancellation, reads the old MongoDB state and writes it into Redis after another process has deleted the key. Key deletion alone cannot rule out this race. A production implementation needs an ordering or versioning strategy. That race is explained here rather than solved in this exercise.

## When services stop

With Redis stopped and MongoDB running, the `search` scenario should print `cache unavailable` and return the MongoDB result. With both services stopped, it should exit with a search error. Returning [] would be misleading because that means no matching journeys, not that the data source failed.

I use short Redis connection and operation timeouts but still measure the real delay. A configured timeout does not promise that the entire failure path always finishes in exactly that time. Service outage checks are in the GitHub Actions workflow and can also be repeated using the README commands.

## Concurrent searches

I remove the exact cache key, delay the MongoDB query by 200 milliseconds and launch ten searches for the same request. Every result must contain LAB05-T-OK. Several of the searches may miss Redis before the first response is stored, so more than one MongoDB lookup is possible. The scenario prints the actual number. A later search before expiry should be served by Redis without another MongoDB call.

Preventing this duplicate work is optional in the lab. One approach is to coalesce identical in-flight requests in the application. A distributed lock would be needed if multiple application instances had to coordinate their cache fill.

## Seat counter

I store two temporary values in Redis, one with zero remaining seats and one with ten. The Lua script `decrement_if_positive.lua` reads and checks one counter atomically before decrementing it. Zero stays zero, ten becomes nine and an unknown key produces an error instead of silently becoming zero.

The code can be checked with `dotnet run --project database/redis/lecture06/CacheLab -- seats`. It also works directly with Redis CLI:

```sh
docker compose exec -T redis redis-cli SET availability:LAB06:TRIP-ZERO 0 EX 60
docker compose exec -T redis redis-cli SET availability:LAB06:TRIP-TEN 10 EX 60
docker compose exec -T redis redis-cli --eval /scripts/lecture06/decrement_if_positive.lua availability:LAB06:TRIP-ZERO
docker compose exec -T redis redis-cli --eval /scripts/lecture06/decrement_if_positive.lua availability:LAB06:TRIP-TEN
```

The script updates a Redis counter only. It does not reserve a seat in PostgreSQL, create a ticket or accept payment. A successful counter decrement is not a completed purchase.

## Which data belongs in Redis?

The worksheet suggests 30 minutes for search results, eight hours for route/day seat counts, and 24 hours each for route and stop information. I would not reuse all of those expiries unchanged.

| Cached data | Example key and value | Source | My TTL | When to remove or refresh |
| --- | --- | --- | --- | --- |
| Journey search | `search:LAB06:v2:...` containing a versioned JSON journey array | MongoDB journey_search | 20 seconds | Delete affected keys after a departure update or cancellation; TTL covers missed invalidations |
| Route/day seat count | `availability:LAB06:TRIP-TEN` = `10` | Redis exercise counter; real booking data would need an authoritative reservation store | 60 seconds in the exercise | Expire the demonstration value; never trust a cached count alone to complete a booking |
| Route information | `route:LAB06:LINE-M2` = `{"routeId":"LINE-M2","cityId":"CPH"}` | Route identifiers from the MongoDB fixture | 24 hours | Delete on route changes and keep TTL as a fallback |
| Stop information | `stop:LAB06:STOP-NORREPORT` = `{"stopId":"STOP-NORREPORT","cityId":"CPH"}` | Stop identifiers from the MongoDB fixture | 24 hours | Delete on stop changes and keep TTL as a fallback |

For the route and stop examples, the fixture only supplies identifiers; this is not a complete route or stop reference service. The `data` scenario runs SET, GET, TTL and DEL against those two values. They do not need a journey-search cache function.

Thirty minutes feels too long for journey results that can change through cancellations or seat availability. Eight hours is especially unsuitable for a seat count that passengers may act on. Route and stop identifiers generally change less often, so I can accept a longer expiry as long as updates invalidate the affected entries.

If Redis is unavailable, journey searches should continue through MongoDB. The application should not sell seats using an old cached number. If updates are missed, even a 20-second result can briefly be stale. Two passengers searching one minute apart do not share the same 20-second entry: it has expired by then. They can share an entry only while it is still alive and only if all five inputs are identical.

## Repeating the full check

```sh
docker compose up -d mongo redis
docker compose exec -T mongo mongosh --quiet --file /scripts/lecture05/reset.js
dotnet run --project database/redis/lecture06/CacheLab -- verify
```

The GitHub Actions job is a second way to run the tests on an environment that has .NET and Docker. These notes describe the test method and expected observations; pass or fail should be taken from the actual command output.

## Results from GitHub Actions, 18 October 2026

I ran the exercise on a GitHub-hosted Ubuntu runner with .NET 8, MongoDB and Redis containers. [The first integration run](https://github.com/AhmadAmerBakran/mobilityticketing-lecture-6/actions/runs/39133970865) completed successfully.

The first morning search took 7.5 ms using MongoDB. The repeated search took 2.2 ms from Redis without an additional MongoDB call. The empty evening search returned [] in both cases; the Redis hit took 0.3 ms. After the one-second expiry and 1.5-second wait, the next search used MongoDB again.

All three invalid-value cases caused a new MongoDB lookup and cache replacement. The cancellation test showed LAB05-T-OK coming from the stale cache after MongoDB had been updated. Deleting the key changed the next response to []. The tests for key uniqueness, the Lua seat counter, and storing route and stop values also passed.

Ten simultaneous searches caused **10 MongoDB calls** with the deliberate 200 ms delay. A search immediately afterward used Redis and did not increase the call count. I did not add the optional in-flight request coalescing, so this is the expected stampede behavior for the experiment.

When Redis was stopped, the search still found LAB05-T-OK through MongoDB. That fallback search took **1864.8 ms**, despite the shorter configured Redis operation timeouts. With both services stopped, the program exited with `Journey search failed: MongoDB is unavailable.` After restarting the services, the search succeeded again and took 124.7 ms on that run.

The timings are measurements from this CI runner, not fixed performance targets. The successful build and all test steps are visible in the linked workflow log.
