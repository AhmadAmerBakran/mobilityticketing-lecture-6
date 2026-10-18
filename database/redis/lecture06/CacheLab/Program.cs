using StackExchange.Redis;

var scenario = args.Length > 0 ? args[0].ToLowerInvariant() : "repeat";
var redisOptions = ConfigurationOptions.Parse(
    Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "127.0.0.1:6379");
redisOptions.AbortOnConnectFail = false;
redisOptions.ConnectRetry = 0;
redisOptions.ConnectTimeout = 500;
redisOptions.SyncTimeout = 500;
redisOptions.AsyncTimeout = 500;

IConnectionMultiplexer? redis = null;
try
{
    redis = await ConnectionMultiplexer.ConnectAsync(redisOptions);
}
catch (RedisException)
{
    Console.WriteLine("cache unavailable");
}
catch (TimeoutException)
{
    Console.WriteLine("cache unavailable");
}

try
{
    await LabScenarios.Run(scenario, new MongoJourneySearch(), redis);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}
finally
{
    redis?.Dispose();
}
