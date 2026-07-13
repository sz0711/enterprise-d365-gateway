using System.Globalization;
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
                JsonValueKind.String when TryParseUtcDateTime(element.GetString(), out var dtValue) => dtValue,
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

        /// <summary>
        /// Parses ISO-8601 date strings deterministically: offset-less values are
        /// interpreted as UTC (never as the host's local timezone), so stored
        /// instants and key signatures are identical on every machine.
        /// Only strict ISO-8601 shapes are accepted to avoid coercing ordinary
        /// strings (e.g. "2024-01" account numbers stay strings).
        /// </summary>
        internal static bool TryParseUtcDateTime(string? text, out DateTime result)
        {
            result = default;
            if (string.IsNullOrEmpty(text) || text.Length < 10 || text[4] != '-' || text[7] != '-')
                return false;

            if (!DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var dto))
            {
                return false;
            }

            result = dto.UtcDateTime;
            return true;
        }
    }
}
