using System.Reflection;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using MODEL;
using enterprise_d365_gateway.Interfaces;
using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Services
{
    public class EarlyboundEntityMapper : IEarlyboundEntityMapper
    {
        /// <summary>
        /// Attribute-name prefixes that must never be writable through the
        /// gateway (portal identity secrets such as adx_identity_passwordhash).
        /// </summary>
        private static readonly string[] RestrictedAttributePrefixes = { "adx_identity_" };

        private readonly record struct AttributeInfo(string LogicalName, Type Type);

        private readonly IReadOnlyDictionary<string, Type> _entityTypeByLogicalName;
        private readonly IReadOnlyDictionary<Type, IReadOnlyDictionary<string, AttributeInfo>> _attributeInfoByEntityType;

        public EarlyboundEntityMapper()
        {
            var modelAssembly = typeof(Account).Assembly;

            var entityTypes = modelAssembly
                .GetTypes()
                .Where(t => typeof(Entity).IsAssignableFrom(t) && !t.IsAbstract)
                .Select(t => new
                {
                    Type = t,
                    LogicalName = t.GetCustomAttribute<EntityLogicalNameAttribute>()?.LogicalName
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.LogicalName))
                .ToList();

            _entityTypeByLogicalName = entityTypes.ToDictionary(
                x => x.LogicalName!,
                x => x.Type,
                StringComparer.OrdinalIgnoreCase);

            _attributeInfoByEntityType = entityTypes
                .Select(x => x.Type)
                .Distinct()
                .ToDictionary(t => t, BuildAttributeInfoMap);
        }

        public void ValidatePayload(UpsertPayload payload)
        {
            ValidateAndGetEntityMetadata(payload);
        }

        public Entity MapToEntity(UpsertPayload payload)
        {
            var (entityType, attributeInfoMap) = ValidateAndGetEntityMetadata(payload);

            var entity = (Entity)Activator.CreateInstance(entityType)!;

            if (payload.Id.HasValue && payload.Id.Value != Guid.Empty)
            {
                entity.Id = payload.Id.Value;
            }

            foreach (var kvp in payload.Attributes)
            {
                var info = attributeInfoMap[kvp.Key];
                entity[info.LogicalName] = ConvertToExpectedType(kvp.Value, info.Type, info.LogicalName);
            }

            if (payload.KeyAttributes != null)
            {
                foreach (var keyAttribute in payload.KeyAttributes)
                {
                    if (payload.Attributes.ContainsKey(keyAttribute.Key))
                    {
                        continue;
                    }

                    var info = attributeInfoMap[keyAttribute.Key];
                    entity[info.LogicalName] = ConvertToExpectedType(
                        keyAttribute.Value,
                        info.Type,
                        info.LogicalName);
                }
            }

            return entity;
        }

        public IReadOnlyList<string> ValidateLookup(string path, LookupDefinition lookup)
        {
            var errors = new List<string>();

            if (!_entityTypeByLogicalName.TryGetValue(lookup.EntityLogicalName, out var entityType)
                || !_attributeInfoByEntityType.TryGetValue(entityType, out var attributeInfoMap))
            {
                errors.Add($"{path}: Entity '{lookup.EntityLogicalName}' does not exist in the early-bound model.");
                return errors;
            }

            foreach (var keyAttribute in lookup.KeyAttributes)
            {
                ValidateAttributeName(attributeInfoMap, keyAttribute.Key, lookup.EntityLogicalName, $"{path}.KeyAttributes", errors);
            }

            if (lookup.CreateAttributes != null)
            {
                foreach (var createAttribute in lookup.CreateAttributes)
                {
                    ValidateAttributeName(attributeInfoMap, createAttribute.Key, lookup.EntityLogicalName, $"{path}.CreateAttributes", errors);
                }
            }

            return errors;
        }

        public object? ConvertQueryValue(string entityLogicalName, string attributeName, object? value)
        {
            if (!TryGetAttributeInfo(entityLogicalName, attributeName, out var info))
            {
                return DataverseValueNormalizer.Normalize(value);
            }

            var converted = ConvertToExpectedType(value, info.Type, info.LogicalName);

            // QueryExpression conditions want raw primitives, not SDK wrappers.
            return converted switch
            {
                OptionSetValue osv => osv.Value,
                Money money => money.Value,
                EntityReference reference => reference.Id,
                _ => converted
            };
        }

        public object? ConvertWriteValue(string entityLogicalName, string attributeName, object? value)
        {
            if (!TryGetAttributeInfo(entityLogicalName, attributeName, out var info))
            {
                return DataverseValueNormalizer.Normalize(value);
            }

            return ConvertToExpectedType(value, info.Type, info.LogicalName);
        }

        private bool TryGetAttributeInfo(string entityLogicalName, string attributeName, out AttributeInfo info)
        {
            info = default;
            return _entityTypeByLogicalName.TryGetValue(entityLogicalName, out var entityType)
                && _attributeInfoByEntityType.TryGetValue(entityType, out var attributeInfoMap)
                && attributeInfoMap.TryGetValue(attributeName, out info);
        }

        private static void ValidateAttributeName(
            IReadOnlyDictionary<string, AttributeInfo> attributeInfoMap,
            string attributeName,
            string entityLogicalName,
            string path,
            List<string> errors)
        {
            if (!attributeInfoMap.ContainsKey(attributeName))
            {
                errors.Add($"{path}: Field '{attributeName}' is not defined on entity '{entityLogicalName}'.");
            }
            else if (IsRestrictedAttribute(attributeName))
            {
                errors.Add($"{path}: Field '{attributeName}' is not writable through this endpoint.");
            }
        }

        private static bool IsRestrictedAttribute(string attributeName)
        {
            foreach (var prefix in RestrictedAttributePrefixes)
            {
                if (attributeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private (Type EntityType, IReadOnlyDictionary<string, AttributeInfo> AttributeInfoMap) ValidateAndGetEntityMetadata(UpsertPayload payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (string.IsNullOrWhiteSpace(payload.EntityLogicalName))
                throw new ArgumentException("EntityLogicalName is required.", nameof(payload.EntityLogicalName));
            if (payload.Attributes == null)
                throw new ArgumentException("Attributes are required.", nameof(payload.Attributes));

            if (!_entityTypeByLogicalName.TryGetValue(payload.EntityLogicalName, out var entityType))
            {
                throw new PayloadValidationException(new[]
                {
                    $"Entity '{payload.EntityLogicalName}' does not exist in the early-bound model."
                });
            }

            if (!_attributeInfoByEntityType.TryGetValue(entityType, out var attributeInfoMap))
            {
                throw new PayloadValidationException(new[]
                {
                    $"Unable to resolve attribute metadata for entity '{payload.EntityLogicalName}'."
                });
            }

            var validationErrors = new List<string>();

            foreach (var kvp in payload.Attributes)
            {
                if (!attributeInfoMap.TryGetValue(kvp.Key, out var info))
                {
                    validationErrors.Add($"Field '{kvp.Key}' is not defined on entity '{payload.EntityLogicalName}'.");
                    continue;
                }

                if (IsRestrictedAttribute(kvp.Key))
                {
                    validationErrors.Add($"Field '{kvp.Key}' is not writable through this endpoint.");
                    continue;
                }

                if (!IsValueCompatible(info.Type, kvp.Value))
                {
                    validationErrors.Add(
                        $"Field '{kvp.Key}' has invalid type. Expected '{GetFriendlyTypeName(info.Type)}'.");
                }
            }

            if (payload.KeyAttributes == null || payload.KeyAttributes.Count == 0)
            {
                validationErrors.Add("KeyAttributes must contain at least one entry.");
            }
            else
            {
                foreach (var keyAttribute in payload.KeyAttributes)
                {
                    if (!attributeInfoMap.TryGetValue(keyAttribute.Key, out var keyInfo))
                    {
                        validationErrors.Add(
                            $"KeyAttribute '{keyAttribute.Key}' is not defined on entity '{payload.EntityLogicalName}'.");
                        continue;
                    }

                    if (IsRestrictedAttribute(keyAttribute.Key))
                    {
                        validationErrors.Add(
                            $"KeyAttribute '{keyAttribute.Key}' is not usable through this endpoint.");
                        continue;
                    }

                    if (!IsValueCompatible(keyInfo.Type, keyAttribute.Value))
                    {
                        validationErrors.Add(
                            $"KeyAttribute '{keyAttribute.Key}' has invalid type. Expected '{GetFriendlyTypeName(keyInfo.Type)}'.");
                    }

                    if (payload.Attributes.ContainsKey(keyAttribute.Key))
                    {
                        validationErrors.Add(
                            $"KeyAttribute '{keyAttribute.Key}' must not also be present in Attributes.");
                    }
                }
            }

            if (validationErrors.Count > 0)
            {
                throw new PayloadValidationException(validationErrors);
            }

            return (entityType, attributeInfoMap);
        }

        private static IReadOnlyDictionary<string, AttributeInfo> BuildAttributeInfoMap(Type entityType)
        {
            var result = new Dictionary<string, AttributeInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var logicalNameAttribute = property.GetCustomAttribute<AttributeLogicalNameAttribute>();
                if (logicalNameAttribute == null)
                {
                    continue;
                }

                var logicalName = logicalNameAttribute.LogicalName;
                var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

                if (typeof(Entity).IsAssignableFrom(propertyType) && propertyType != typeof(Entity))
                {
                    result[logicalName] = new AttributeInfo(logicalName, typeof(EntityReference));
                }
                else
                {
                    result[logicalName] = new AttributeInfo(logicalName, propertyType);
                }
            }

            return result;
        }

        private static bool IsValueCompatible(Type expectedType, object? value)
        {
            if (value == null)
            {
                return true;
            }

            var normalizedExpected = Nullable.GetUnderlyingType(expectedType) ?? expectedType;

            if (normalizedExpected.IsInstanceOfType(value))
            {
                return true;
            }

            if (value is JsonElement jsonElement)
            {
                return IsJsonElementCompatible(normalizedExpected, jsonElement);
            }

            // Early-bound choice columns are generated as enums but stored as OptionSetValue.
            if (normalizedExpected.IsEnum)
                return value is OptionSetValue
                    || value is byte or short or int
                    || (value is string enumName && Enum.TryParse(normalizedExpected, enumName, ignoreCase: true, out _));

            if (normalizedExpected == typeof(string))
                return value is string;

            if (normalizedExpected == typeof(Guid))
                return value is Guid || (value is string s && Guid.TryParse(s, out _));

            if (normalizedExpected == typeof(bool))
                return value is bool;

            if (normalizedExpected == typeof(int))
                return value is byte or short or int;

            if (normalizedExpected == typeof(long))
                return value is byte or short or int or long;

            if (normalizedExpected == typeof(decimal))
                return value is byte or short or int or long or float or double or decimal;

            if (normalizedExpected == typeof(double))
                return value is byte or short or int or long or float or double or decimal;

            if (normalizedExpected == typeof(float))
                return value is byte or short or int or long or float or double or decimal;

            if (normalizedExpected == typeof(DateTime))
                return value is DateTime
                    || value is DateTimeOffset
                    || (value is string dateString && DataverseValueNormalizer.TryParseUtcDateTime(dateString, out _));

            if (normalizedExpected == typeof(Money))
                return value is Money
                    || value is byte or short or int or long or float or double or decimal;

            if (normalizedExpected == typeof(OptionSetValue))
                return value is OptionSetValue
                    || value is byte or short or int;

            if (normalizedExpected == typeof(OptionSetValueCollection))
                return value is OptionSetValueCollection
                    || value is IEnumerable<int>;

            if (normalizedExpected == typeof(EntityReference))
                return value is EntityReference;

            return false;
        }

        private static bool IsJsonElementCompatible(Type expectedType, JsonElement element)
        {
            if (expectedType.IsEnum)
            {
                return element.ValueKind switch
                {
                    JsonValueKind.Null or JsonValueKind.Undefined => true,
                    JsonValueKind.Number => element.TryGetInt32(out _),
                    JsonValueKind.String => Enum.TryParse(expectedType, element.GetString(), ignoreCase: true, out _),
                    _ => false
                };
            }

            return element.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => true,

                JsonValueKind.True or JsonValueKind.False =>
                    expectedType == typeof(bool),

                JsonValueKind.String =>
                    expectedType == typeof(string)
                    || expectedType == typeof(Guid)
                    || expectedType == typeof(DateTime),

                JsonValueKind.Number =>
                    expectedType == typeof(int)
                    || expectedType == typeof(long)
                    || expectedType == typeof(float)
                    || expectedType == typeof(double)
                    || expectedType == typeof(decimal)
                    || expectedType == typeof(Money)
                    || expectedType == typeof(OptionSetValue),

                JsonValueKind.Array =>
                    expectedType == typeof(OptionSetValueCollection)
                    || expectedType == typeof(string),

                JsonValueKind.Object =>
                    expectedType == typeof(string),

                _ => false
            };
        }

        private static object? ConvertToExpectedType(object? value, Type expectedType, string attributeName)
        {
            if (value == null)
            {
                return null;
            }

            var normalizedExpected = Nullable.GetUnderlyingType(expectedType) ?? expectedType;

            if (normalizedExpected.IsInstanceOfType(value))
            {
                // Early-bound enum instances are stored as OptionSetValue in the attribute collection.
                if (normalizedExpected.IsEnum)
                    return new OptionSetValue(Convert.ToInt32(value));
                return value;
            }

            if (value is JsonElement jsonElement)
            {
                return ConvertJsonElementToExpectedType(jsonElement, normalizedExpected, attributeName);
            }

            if (normalizedExpected.IsEnum)
            {
                if (value is OptionSetValue existingOsv) return existingOsv;
                if (value is byte or short or int) return new OptionSetValue(Convert.ToInt32(value));
                if (value is string enumName && Enum.TryParse(normalizedExpected, enumName, ignoreCase: true, out var parsedEnum))
                    return new OptionSetValue(Convert.ToInt32(parsedEnum));
            }

            if (normalizedExpected == typeof(string))
                return Convert.ToString(value);

            if (normalizedExpected == typeof(Guid))
            {
                if (value is Guid guidValue) return guidValue;
                if (value is string guidString && Guid.TryParse(guidString, out var parsedGuid)) return parsedGuid;
            }

            if (normalizedExpected == typeof(bool))
                return Convert.ToBoolean(value);

            if (normalizedExpected == typeof(int))
                return Convert.ToInt32(value);

            if (normalizedExpected == typeof(long))
                return Convert.ToInt64(value);

            if (normalizedExpected == typeof(decimal))
                return Convert.ToDecimal(value);

            if (normalizedExpected == typeof(double))
                return Convert.ToDouble(value);

            if (normalizedExpected == typeof(float))
                return Convert.ToSingle(value);

            if (normalizedExpected == typeof(DateTime))
            {
                if (value is DateTime dt) return dt;
                if (value is DateTimeOffset dto) return dto.UtcDateTime;
                if (value is string dateString && DataverseValueNormalizer.TryParseUtcDateTime(dateString, out var parsedDt))
                    return parsedDt;
            }

            if (normalizedExpected == typeof(Money))
                return new Money(Convert.ToDecimal(value));

            if (normalizedExpected == typeof(OptionSetValue))
                return new OptionSetValue(Convert.ToInt32(value));

            if (normalizedExpected == typeof(OptionSetValueCollection))
            {
                if (value is IEnumerable<int> intValues)
                {
                    var collection = new OptionSetValueCollection();
                    foreach (var intValue in intValues)
                    {
                        collection.Add(new OptionSetValue(intValue));
                    }
                    return collection;
                }
            }

            if (normalizedExpected == typeof(EntityReference) && value is EntityReference entityReference)
                return entityReference;

            throw new PayloadValidationException(new[]
            {
                $"Field '{attributeName}' could not be converted to '{GetFriendlyTypeName(expectedType)}'."
            });
        }

        private static object? ConvertJsonElementToExpectedType(JsonElement element, Type expectedType, string attributeName)
        {
            if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return null;
            }

            try
            {
                if (expectedType.IsEnum)
                {
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var enumInt))
                        return new OptionSetValue(enumInt);
                    if (element.ValueKind == JsonValueKind.String
                        && Enum.TryParse(expectedType, element.GetString(), ignoreCase: true, out var parsedEnum))
                        return new OptionSetValue(Convert.ToInt32(parsedEnum));
                }

                if (expectedType == typeof(string))
                {
                    return element.ValueKind == JsonValueKind.String
                        ? element.GetString()
                        : element.GetRawText();
                }

                if (expectedType == typeof(Guid))
                {
                    if (element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out var guid))
                        return guid;
                }

                if (expectedType == typeof(bool))
                {
                    if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        return element.GetBoolean();
                }

                if (expectedType == typeof(int))
                {
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var intValue))
                        return intValue;
                }

                if (expectedType == typeof(long))
                {
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var longValue))
                        return longValue;
                }

                if (expectedType == typeof(decimal))
                {
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var decimalValue))
                        return decimalValue;
                }

                if (expectedType == typeof(double))
                {
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var doubleValue))
                        return doubleValue;
                }

                if (expectedType == typeof(float))
                {
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out var floatValue))
                        return floatValue;
                }

                if (expectedType == typeof(DateTime))
                {
                    if (element.ValueKind == JsonValueKind.String &&
                        DataverseValueNormalizer.TryParseUtcDateTime(element.GetString(), out var parsedDt))
                    {
                        return parsedDt;
                    }
                }

                if (expectedType == typeof(Money))
                {
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var moneyValue))
                        return new Money(moneyValue);
                }

                if (expectedType == typeof(OptionSetValue))
                {
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var optionValue))
                        return new OptionSetValue(optionValue);
                }

                if (expectedType == typeof(OptionSetValueCollection))
                {
                    if (element.ValueKind == JsonValueKind.Array)
                    {
                        var collection = new OptionSetValueCollection();
                        foreach (var item in element.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var option))
                            {
                                collection.Add(new OptionSetValue(option));
                            }
                            else
                            {
                                throw new PayloadValidationException(new[]
                                {
                                    $"Field '{attributeName}' contains an invalid multi-select option value."
                                });
                            }
                        }
                        return collection;
                    }
                }

                if (expectedType == typeof(EntityReference))
                {
                    throw new PayloadValidationException(new[]
                    {
                        $"Field '{attributeName}' expects EntityReference. This must be provided via lookup resolution, not raw JSON."
                    });
                }
            }
            catch (PayloadValidationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PayloadValidationException(new[]
                {
                    $"Field '{attributeName}' could not be converted to '{GetFriendlyTypeName(expectedType)}'. Error: {ex.Message}"
                });
            }

            throw new PayloadValidationException(new[]
            {
                $"Field '{attributeName}' could not be converted to '{GetFriendlyTypeName(expectedType)}'."
            });
        }

        private static string GetFriendlyTypeName(Type type)
        {
            var normalized = Nullable.GetUnderlyingType(type) ?? type;

            if (normalized.IsEnum) return $"OptionSetValue ({normalized.Name})";
            if (normalized == typeof(string)) return "string";
            if (normalized == typeof(Guid)) return "guid";
            if (normalized == typeof(bool)) return "bool";
            if (normalized == typeof(int)) return "int";
            if (normalized == typeof(long)) return "long";
            if (normalized == typeof(decimal)) return "decimal";
            if (normalized == typeof(double)) return "double";
            if (normalized == typeof(float)) return "float";
            if (normalized == typeof(DateTime)) return "datetime";
            if (normalized == typeof(Money)) return "Money";
            if (normalized == typeof(OptionSetValue)) return "OptionSetValue";
            if (normalized == typeof(OptionSetValueCollection)) return "OptionSetValueCollection";
            if (normalized == typeof(EntityReference)) return "EntityReference";

            return normalized.Name;
        }
    }
}
