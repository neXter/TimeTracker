using System.Text.Json;
using System.Text.Json.Serialization;

namespace TimeTracker.Core;

public record AppRow(long Id, string ExePath, string ExeName, string DetectedName, string Source, long TitleRuleId, long FirstSeenUtc);

public record Alias(long Id, string MatchKind, string MatchValue, string? TitleContains, string DisplayName, bool Hidden);

public record WindowInfo(int Pid, string ExePath, string Title);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };
    public static string Serialize(object? o) => JsonSerializer.Serialize(o, Options);
    public static T? Deserialize<T>(string s) => JsonSerializer.Deserialize<T>(s, Options);
}

/// <summary>Source of wall-clock time; replaced in tests.</summary>
public static class Time
{
    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
