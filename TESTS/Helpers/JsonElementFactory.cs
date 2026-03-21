using System.Text.Json;

namespace enterprise_d365_gateway.Tests.Helpers;

/// <summary>
/// Creates JsonElement values from primitives without leaking JsonDocument (IDisposable).
/// Uses the Clone() pattern — the returned JsonElement is independent of the disposed document.
/// </summary>
public static class JsonElementFactory
{
    public static JsonElement From(string value) => Parse($"\"{EscapeJson(value)}\"");
    public static JsonElement From(int value) => Parse(value.ToString());
    public static JsonElement From(long value) => Parse(value.ToString());
    public static JsonElement From(double value) => Parse(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public static JsonElement From(decimal value) => Parse(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public static JsonElement From(bool value) => Parse(value ? "true" : "false");
    public static JsonElement From(Guid value) => Parse($"\"{value}\"");
    public static JsonElement From(DateTime value) => Parse($"\"{value:O}\"");
    public static JsonElement FromNull() => Parse("null");

    public static JsonElement FromObject(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        return Parse(json);
    }

    public static JsonElement FromArray(params object[] items)
    {
        var json = JsonSerializer.Serialize(items);
        return Parse(json);
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string EscapeJson(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
