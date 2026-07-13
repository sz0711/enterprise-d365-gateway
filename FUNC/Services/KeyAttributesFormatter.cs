using System.Globalization;
using System.Text;

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
                .Select(kvp => $"{Escape(kvp.Key.ToLowerInvariant())}={Escape(FormatValue(kvp.Value))}");

            return $"{entityLogicalName.ToLowerInvariant()}:{string.Join(",", orderedPairs)}";
        }

        /// <summary>
        /// Escapes signature separator characters so that values containing
        /// ',' '=' ':' or '\' can never collide with a different key set
        /// (signatures drive the shared cache and the keyed locks).
        /// </summary>
        private static readonly char[] SeparatorChars = { '\\', ',', '=', ':' };

        private static string Escape(string value)
        {
            if (value.IndexOfAny(SeparatorChars) < 0)
                return value;

            var sb = new StringBuilder(value.Length + 4);
            foreach (var c in value)
            {
                if (c is '\\' or ',' or '=' or ':')
                    sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
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
