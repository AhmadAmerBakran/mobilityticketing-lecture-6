using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;

public sealed record CacheEntry(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] DateTimeOffset CachedAtUtc,
    [property: JsonRequired] List<Journey> Items);

public sealed record SearchResult(
    string CacheKey,
    string DataSource,
    string CacheStatus,
    List<Journey> Items,
    int MongoCalls,
    TimeSpan Elapsed);

public sealed class CachedJourneySearch
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly MongoJourneySearch source;
    private readonly IConnectionMultiplexer? redis;
    private readonly TimeSpan lifetime;

    public CachedJourneySearch(MongoJourneySearch source, IConnectionMultiplexer? redis,
        TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime));

        this.source = source;
        this.redis = redis;
        this.lifetime = lifetime;
    }

    public async Task<SearchResult> Search(SearchRequest request)
    {
        var key = CacheKey.ForSearch(request);
        var clock = Stopwatch.StartNew();
        var status = redis is null ? "unavailable" : "miss";

        if (redis is not null)
        {
            try
            {
                var cached = await redis.GetDatabase().StringGetAsync(key);
                if (!cached.IsNull)
                {
                    if (TryRead(cached.ToString(), out var journeys))
                    {
                        clock.Stop();
                        return new SearchResult(key, "redis", "hit", journeys,
                            source.Calls, clock.Elapsed);
                    }

                    status = "invalid";
                    Console.WriteLine("cache invalid");
                }
            }
            catch (RedisException)
            {
                status = "unavailable";
                Console.WriteLine("cache unavailable");
            }
            catch (TimeoutException)
            {
                status = "unavailable";
                Console.WriteLine("cache unavailable");
            }
        }
        else
        {
            Console.WriteLine("cache unavailable");
        }

        List<Journey> result;
        try
        {
            result = await source.Fetch(request);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Journey search failed: MongoDB is unavailable.", ex);
        }

        if (redis is not null)
        {
            try
            {
                var entry = new CacheEntry(2, DateTimeOffset.UtcNow, result);
                var json = JsonSerializer.Serialize(entry, JsonOptions);
                await redis.GetDatabase().StringSetAsync(key, json, lifetime);
            }
            catch (RedisException)
            {
                status = "unavailable";
                Console.WriteLine("cache unavailable");
            }
            catch (TimeoutException)
            {
                status = "unavailable";
                Console.WriteLine("cache unavailable");
            }
        }

        clock.Stop();
        return new SearchResult(key, "mongodb", status, result,
            source.Calls, clock.Elapsed);
    }

    private static bool TryRead(string raw, out List<Journey> items)
    {
        items = new List<Journey>();

        try
        {
            var entry = JsonSerializer.Deserialize<CacheEntry>(raw, JsonOptions);
            if (entry is null || entry.SchemaVersion != 2 ||
                entry.CachedAtUtc == default ||
                entry.CachedAtUtc.Offset != TimeSpan.Zero ||
                entry.Items is null || entry.Items.Any(j => j is null))
                return false;

            items = entry.Items;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
