using System.Globalization;
using System.Text.Json;
using StackExchange.Redis;

public static class LabScenarios
{
    private static readonly SearchRequest Morning = new(
        "CPH", "STOP-NORREPORT", "STOP-AIRPORT",
        DateTimeOffset.Parse("2026-10-02T06:00:00Z", CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-10-02T07:00:00Z", CultureInfo.InvariantCulture));

    private static readonly SearchRequest Evening = Morning with
    {
        Start = DateTimeOffset.Parse("2026-10-02T18:00:00Z", CultureInfo.InvariantCulture),
        End = DateTimeOffset.Parse("2026-10-02T19:00:00Z", CultureInfo.InvariantCulture)
    };

    public static async Task Run(string scenario, MongoJourneySearch source,
        IConnectionMultiplexer? redis)
    {
        if (scenario == "keys")
        {
            CheckKeys();
            return;
        }

        if (scenario == "search")
        {
            Print(await new CachedJourneySearch(source, redis,
                TimeSpan.FromSeconds(20)).Search(Morning));
            return;
        }

        if (redis is null || !redis.IsConnected)
            throw new InvalidOperationException(
                "Redis must be available to run this lab scenario.");

        var db = redis.GetDatabase();

        switch (scenario)
        {
            case "collision":
                await CheckCollision(source, db);
                break;
            case "repeat":
                await CheckRepeat(source, redis);
                break;
            case "empty":
                await CheckEmpty(source, redis);
                break;
            case "expiry":
                await CheckExpiry(source, redis);
                break;
            case "invalid":
                await CheckInvalid(source, redis);
                break;
            case "cancel":
                await CheckCancellation(source, redis);
                break;
            case "concurrent":
                await CheckConcurrent(source, redis);
                break;
            case "seats":
                await CheckSeats(db);
                break;
            case "data":
                await CheckData(db);
                break;
            case "verify":
                CheckKeys();
                await CheckCollision(source, db);
                await CheckRepeat(source, redis);
                await CheckEmpty(source, redis);
                await CheckExpiry(source, redis);
                await CheckInvalid(source, redis);
                await CheckCancellation(source, redis);
                await CheckConcurrent(source, redis);
                await CheckSeats(db);
                await CheckData(db);
                Console.WriteLine("All connected Redis and MongoDB checks passed.");
                break;
            default:
                throw new ArgumentException("Unknown scenario: " + scenario);
        }
    }

    private static CachedJourneySearch Cache(MongoJourneySearch source,
        IConnectionMultiplexer redis, int seconds = 20) =>
        new(source, redis, TimeSpan.FromSeconds(seconds));

    private static void CheckKeys()
    {
        var key = CacheKey.ForSearch(Morning);
        Assert(key != CacheKey.ForSearch(Evening), "Time ranges must have different keys.");
        Assert(key != CacheKey.ForSearch(Morning with { CityId = "cph" }),
            "Keys must remain case-sensitive.");
        Assert(key != CacheKey.ForSearch(Morning with { FromStopId = "STOP-AIRPORT" }),
            "Different origins must have different keys.");
        Assert(key != CacheKey.ForSearch(Morning with { ToStopId = "STOP-NORREPORT" }),
            "Different destinations must have different keys.");
        var equivalentStart = Morning.Start.ToOffset(TimeSpan.FromHours(2));
        var equivalentEnd = Morning.End.ToOffset(TimeSpan.FromHours(2));
        Assert(key == CacheKey.ForSearch(Morning with
            { Start = equivalentStart, End = equivalentEnd }),
            "Equivalent UTC instants must share a key.");
        Assert(key.Contains("v2"), "The key needs a version.");
        Assert(CacheKey.ForSearch(Morning with { CityId = "CPH:X" }).Contains("CPH%3AX"),
            "Separators inside IDs must be escaped.");
        ExpectArgumentError(Morning with { CityId = "" });
        ExpectArgumentError(Morning with { FromStopId = " " });
        ExpectArgumentError(Morning with { End = Morning.Start });
        ExpectArgumentError(Morning with { End = Morning.Start.AddSeconds(-1) });
        Console.WriteLine("PASS keys: ranges, direction, case, UTC, escaping, validation.");
        Console.WriteLine("morningKey=" + key);
        Console.WriteLine("eveningKey=" + CacheKey.ForSearch(Evening));
    }

    private static void ExpectArgumentError(SearchRequest request)
    {
        try
        {
            _ = CacheKey.ForSearch(request);
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException("An invalid search request was accepted.");
    }

    private static async Task CheckCollision(MongoJourneySearch source, IDatabase db)
    {
        const string badKey = "search:LAB06:CPH:STOP-NORREPORT:STOP-AIRPORT";
        await db.KeyDeleteAsync(badKey);
        try
        {
            var morning = await source.Fetch(Morning);
            await db.StringSetAsync(badKey, JsonSerializer.Serialize(morning),
                TimeSpan.FromMinutes(5));
            var actualEvening = await source.Fetch(Evening);
            var wronglyCached = JsonSerializer.Deserialize<List<Journey>>(
                (await db.StringGetAsync(badKey)).ToString());
            Assert(HasTrip(morning) && actualEvening.Count == 0 &&
                wronglyCached is not null && HasTrip(wronglyCached),
                "The deliberately incomplete key must demonstrate the wrong result.");
            Console.WriteLine("PASS collision: the incomplete key returns a morning trip " +
                "for an empty evening search.");
            Console.WriteLine("badKey=" + badKey);
        }
        finally
        {
            await db.KeyDeleteAsync(badKey);
        }
    }

    private static async Task CheckRepeat(MongoJourneySearch source,
        IConnectionMultiplexer redis)
    {
        await redis.GetDatabase().KeyDeleteAsync(CacheKey.ForSearch(Morning));
        var search = Cache(source, redis);
        var before = source.Calls;
        var first = await search.Search(Morning);
        var second = await search.Search(Morning);
        Print(first);
        Print(second);
        Assert(first.DataSource == "mongodb" && HasTrip(first.Items) &&
            second.DataSource == "redis" && HasTrip(second.Items) &&
            source.Calls - before == 1,
            "The repeated search must use MongoDB once and Redis once.");
        Console.WriteLine("PASS repeat.");
    }

    private static async Task CheckEmpty(MongoJourneySearch source,
        IConnectionMultiplexer redis)
    {
        await redis.GetDatabase().KeyDeleteAsync(CacheKey.ForSearch(Evening));
        var search = Cache(source, redis);
        var before = source.Calls;
        var first = await search.Search(Evening);
        var second = await search.Search(Evening);
        Print(first);
        Print(second);
        Assert(first.Items.Count == 0 && second.Items.Count == 0 &&
            second.DataSource == "redis" && source.Calls - before == 1,
            "An empty list must be a valid cache hit.");
        Console.WriteLine("PASS empty.");
    }

    private static async Task CheckExpiry(MongoJourneySearch source,
        IConnectionMultiplexer redis)
    {
        var key = CacheKey.ForSearch(Morning);
        var db = redis.GetDatabase();
        await db.KeyDeleteAsync(key);
        var search = Cache(source, redis, 1);
        var before = source.Calls;
        var first = await search.Search(Morning);
        var ttl = await db.KeyTimeToLiveAsync(key);
        Assert(ttl is not null && ttl > TimeSpan.Zero,
            "The cache key should have a TTL.");
        await Task.Delay(1500);
        var second = await search.Search(Morning);
        Print(first);
        Print(second);
        Assert(second.DataSource == "mongodb" && source.Calls - before == 2,
            "A search after expiry must query MongoDB.");
        Console.WriteLine("PASS expiry.");
    }

    private static async Task CheckInvalid(MongoJourneySearch source,
        IConnectionMultiplexer redis)
    {
        var db = redis.GetDatabase();
        var key = CacheKey.ForSearch(Morning);
        var cases = new Dictionary<string, string>
        {
            ["not-json"] = "not-json",
            ["wrong-version"] = "{\"schemaVersion\":3,\"cachedAtUtc\":\"2026-10-02T06:00:00+00:00\",\"items\":[]}",
            ["missing-timestamp"] = "{\"schemaVersion\":2,\"items\":[]}"
        };

        foreach (var sample in cases)
        {
            await db.StringSetAsync(key, sample.Value, TimeSpan.FromSeconds(20));
            var result = await Cache(source, redis).Search(Morning);
            Print(result);
            Assert(result.CacheStatus == "invalid" &&
                result.DataSource == "mongodb" && HasTrip(result.Items),
                sample.Key + " should be treated as a cache miss.");
            var replacement = (await db.StringGetAsync(key)).ToString();
            Assert(replacement.Contains("\"schemaVersion\":2") &&
                replacement.Contains("\"cachedAtUtc\""),
                "An invalid cache entry must be replaced.");
            Console.WriteLine("PASS invalid: " + sample.Key);
        }
    }

    private static async Task CheckCancellation(MongoJourneySearch source,
        IConnectionMultiplexer redis)
    {
        var db = redis.GetDatabase();
        var key = CacheKey.ForSearch(Morning);
        await source.SetLabTripStatus("Scheduled");
        await db.KeyDeleteAsync(key);

        try
        {
            var search = Cache(source, redis, 300);
            var original = await search.Search(Morning);
            Assert(HasTrip(original.Items), "The original trip must be scheduled.");
            await source.SetLabTripStatus("Cancelled");
            var updatedDatabase = await source.Fetch(Morning);
            Assert(updatedDatabase.Count == 0, "MongoDB must reflect cancellation.");
            var stale = await search.Search(Morning);
            Assert(stale.DataSource == "redis" && HasTrip(stale.Items),
                "The cache must demonstrate a stale trip before invalidation.");
            await db.KeyDeleteAsync(key);
            var fresh = await search.Search(Morning);
            Assert(fresh.DataSource == "mongodb" && fresh.Items.Count == 0,
                "Invalidating the cache must expose the cancellation.");
            Print(original);
            Print(stale);
            Print(fresh);
            Console.WriteLine("PASS cancel: stale until key deletion; fresh afterwards.");
        }
        finally
        {
            await source.SetLabTripStatus("Scheduled");
            await db.KeyDeleteAsync(key);
        }
    }

    private static async Task CheckConcurrent(MongoJourneySearch source,
        IConnectionMultiplexer redis)
    {
        var db = redis.GetDatabase();
        await db.KeyDeleteAsync(CacheKey.ForSearch(Morning));
        var search = Cache(source, redis);
        var before = source.Calls;
        source.DelayMilliseconds = 200;
        try
        {
            var results = await Task.WhenAll(
                Enumerable.Range(0, 10).Select(_ => search.Search(Morning)));
            var calls = source.Calls - before;
            Assert(results.All(x => HasTrip(x.Items)) && calls >= 1,
                "Concurrent searches must still return the correct trip.");
            Console.WriteLine("concurrent MongoDB calls=" + calls + " for 10 requests");
            var last = await search.Search(Morning);
            Print(last);
            Assert(last.DataSource == "redis" && source.Calls - before == calls,
                "The next search must use the cached value.");
            Console.WriteLine("PASS concurrent.");
        }
        finally
        {
            source.DelayMilliseconds = 0;
        }
    }

    private static async Task CheckSeats(IDatabase db)
    {
        const string empty = "availability:LAB06:TRIP-ZERO";
        const string ten = "availability:LAB06:TRIP-TEN";
        const string missing = "availability:LAB06:UNKNOWN";
        var script = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "decrement_if_positive.lua"));

        await db.StringSetAsync(empty, 0, TimeSpan.FromSeconds(60));
        await db.StringSetAsync(ten, 10, TimeSpan.FromSeconds(60));
        await db.KeyDeleteAsync(missing);
        var zero = await db.ScriptEvaluateAsync(script, new RedisKey[] { empty });
        var nine = await db.ScriptEvaluateAsync(script, new RedisKey[] { ten });
        Assert(zero.ToString() == "0" && nine.ToString() == "9" &&
            (await db.StringGetAsync(empty)).ToString() == "0",
            "The Lua script must only decrement a positive count.");

        var unknownRejected = false;
        try
        {
            await db.ScriptEvaluateAsync(script, new RedisKey[] { missing });
        }
        catch (RedisServerException ex) when (ex.Message.Contains("availability unknown"))
        {
            unknownRejected = true;
        }

        Assert(unknownRejected, "An unknown seat key must not be treated as zero.");
        Console.WriteLine("PASS seats: zero remains 0, ten becomes 9, missing is unknown.");
        await db.KeyDeleteAsync(empty);
        await db.KeyDeleteAsync(ten);
    }

    private static async Task CheckData(IDatabase db)
    {
        const string route = "route:LAB06:LINE-M2";
        const string stop = "stop:LAB06:STOP-NORREPORT";
        await db.StringSetAsync(route, "{\"routeId\":\"LINE-M2\",\"cityId\":\"CPH\"}",
            TimeSpan.FromHours(24));
        await db.StringSetAsync(stop, "{\"stopId\":\"STOP-NORREPORT\",\"cityId\":\"CPH\"}",
            TimeSpan.FromHours(24));
        Assert((await db.StringGetAsync(route)).ToString().Contains("LINE-M2") &&
            (await db.StringGetAsync(stop)).ToString().Contains("STOP-NORREPORT"),
            "Route and stop keys must be readable.");
        Assert(await db.KeyTimeToLiveAsync(route) is not null &&
            await db.KeyTimeToLiveAsync(stop) is not null,
            "Route and stop keys must expire.");
        await db.KeyDeleteAsync(route);
        await db.KeyDeleteAsync(stop);
        Assert((await db.StringGetAsync(route)).IsNull &&
            (await db.StringGetAsync(stop)).IsNull,
            "Deleted reference data must not be returned.");
        Console.WriteLine("PASS data: SET, GET, TTL and DEL for route and stop.");
    }

    private static void Print(SearchResult result)
    {
        var ids = result.Items.Select(x => x.TripId).ToArray();
        Console.WriteLine("key=" + result.CacheKey +
            " source=" + result.DataSource +
            " cache=" + result.CacheStatus +
            " trips=[" + string.Join(",", ids) + "]" +
            " mongoCalls=" + result.MongoCalls +
            " elapsedMs=" + result.Elapsed.TotalMilliseconds.ToString("F1",
                CultureInfo.InvariantCulture));
    }

    private static bool HasTrip(IEnumerable<Journey> items) =>
        items.Any(x => x.TripId == "LAB05-T-OK");

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
