using System.Globalization;

public static class CacheKey
{
    public static string ForSearch(SearchRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        if (request.End <= request.Start)
            throw new ArgumentException("The search end must be after its start.", nameof(request));

        return string.Join(":",
            "search", "LAB06", "v2",
            Part(request.CityId),
            Part(request.FromStopId),
            Part(request.ToStopId),
            Instant(request.Start),
            Instant(request.End));
    }

    private static string Part(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Search IDs must not be empty.");

        return Uri.EscapeDataString(value);
    }

    private static string Instant(DateTimeOffset value) =>
        value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
}
