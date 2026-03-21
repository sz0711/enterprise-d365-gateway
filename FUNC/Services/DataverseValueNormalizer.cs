using System.Text.Json;

namespace enterprise_d365_gateway.Services
{
    internal static class DataverseValueNormalizer
    {
        public static object? Normalize(object? value)
        {
            if (value is null) return null;
            if (value is not JsonElement element) return value;

            return element.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when element.TryGetGuid(out var guidValue) => guidValue,
                JsonValueKind.String when element.TryGetDateTimeOffset(out var dtoValue) => dtoValue.UtcDateTime,
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number when element.TryGetInt32(out var intValue) => intValue,
                JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
                JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
                JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
                JsonValueKind.Array => element.EnumerateArray().Select(e => Normalize(e)).ToArray(),
                JsonValueKind.Object => element.EnumerateObject()
                    .ToDictionary(p => p.Name, p => Normalize(p.Value)),
                _ => element.ToString()
            };
        }
    }
}
