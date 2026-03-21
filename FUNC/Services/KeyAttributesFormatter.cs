using System.Globalization;

namespace enterprise_d365_gateway.Services
{
    internal static class KeyAttributesFormatter
    {
        public static string BuildSignature(string entityLogicalName, IDictionary<string, object?> keyAttributes)
        {
            if (string.IsNullOrWhiteSpace(entityLogicalName))
            {
                throw new ArgumentException("EntityLogicalName is required.", nameof(entityLogicalName));
            }

            if (keyAttributes == null || keyAttributes.Count == 0)
            {
                throw new ArgumentException("KeyAttributes must contain at least one entry.", nameof(keyAttributes));
            }

            var orderedPairs = keyAttributes
                .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => $"{kvp.Key.ToLowerInvariant()}={FormatValue(kvp.Value)}");

            return $"{entityLogicalName.ToLowerInvariant()}:{string.Join(",", orderedPairs)}";
        }

        private static string FormatValue(object? value)
        {
            var normalized = DataverseValueNormalizer.Normalize(value);
            return normalized switch
            {
                null => "null",
                DateTime dt => dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset dto => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
                _ => normalized.ToString() ?? string.Empty
            };
        }
    }
}
