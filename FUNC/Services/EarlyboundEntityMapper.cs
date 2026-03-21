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
        private readonly IReadOnlyDictionary<string, Type> _entityTypeByLogicalName;
        private readonly IReadOnlyDictionary<Type, IReadOnlyDictionary<string, Type>> _attributeTypeByEntityType;

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

            _attributeTypeByEntityType = entityTypes
                .Select(x => x.Type)
                .Distinct()
                .ToDictionary(t => t, BuildAttributeTypeMap);
        }

        public void ValidatePayload(UpsertPayload payload)
        {
            ValidateAndGetEntityMetadata(payload);
        }

        public Entity MapToEntity(UpsertPayload payload)
        {
            var (entityType, attributeTypeMap) = ValidateAndGetEntityMetadata(payload);

            var entity = (Entity)Activator.CreateInstance(entityType)!;

            if (payload.Id.HasValue && payload.Id.Value != Guid.Empty)
            {
                entity.Id = payload.Id.Value;
            }

            foreach (var kvp in payload.Attributes)
            {
                var attributeName = kvp.Key;
                var expectedType = attributeTypeMap[attributeName];
                entity[attributeName] = ConvertToExpectedType(kvp.Value, expectedType, attributeName);
            }

            if (!string.IsNullOrWhiteSpace(payload.ExternalIdAttribute) && payload.ExternalIdValue is not null)
            {
                if (!payload.Attributes.ContainsKey(payload.ExternalIdAttribute))
                {
                    var expectedType = attributeTypeMap[payload.ExternalIdAttribute];
                    entity[payload.ExternalIdAttribute] = ConvertToExpectedType(
                        payload.ExternalIdValue,
                        expectedType,
                        payload.ExternalIdAttribute);
                }
            }

            return entity;
        }

        private (Type EntityType, IReadOnlyDictionary<string, Type> AttributeTypeMap) ValidateAndGetEntityMetadata(UpsertPayload payload)
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

            if (!_attributeTypeByEntityType.TryGetValue(entityType, out var attributeTypeMap))
            {
                throw new PayloadValidationException(new[]
                {
                    $"Unable to resolve attribute metadata for entity '{payload.EntityLogicalName}'."
                });
            }

            var validationErrors = new List<string>();

            foreach (var kvp in payload.Attributes)
            {
                if (!attributeTypeMap.TryGetValue(kvp.Key, out var expectedType))
                {
                    validationErrors.Add($"Field '{kvp.Key}' is not defined on entity '{payload.EntityLogicalName}'.");
                    continue;
                }

                if (!IsValueCompatible(expectedType, kvp.Value))
                {
                    validationErrors.Add(
                        $"Field '{kvp.Key}' has invalid type. Expected '{GetFriendlyTypeName(expectedType)}'.");
                }
            }

            if (!string.IsNullOrWhiteSpace(payload.ExternalIdAttribute))
            {
                if (!attributeTypeMap.TryGetValue(payload.ExternalIdAttribute, out var externalIdType))
                {
                    validationErrors.Add(
                        $"ExternalIdAttribute '{payload.ExternalIdAttribute}' is not defined on entity '{payload.EntityLogicalName}'.");
                }
                else if (payload.ExternalIdValue is not null && !IsValueCompatible(externalIdType, payload.ExternalIdValue))
                {
                    validationErrors.Add(
                        $"ExternalIdAttribute '{payload.ExternalIdAttribute}' has invalid type. Expected '{GetFriendlyTypeName(externalIdType)}'.");
                }
            }

            if (!string.IsNullOrWhiteSpace(payload.ExternalIdAttribute)
                && payload.Attributes.ContainsKey(payload.ExternalIdAttribute))
            {
                validationErrors.Add(
                    $"ExternalIdAttribute '{payload.ExternalIdAttribute}' must not also be present in Attributes.");
            }

            if (validationErrors.Count > 0)
            {
                throw new PayloadValidationException(validationErrors);
            }

            return (entityType, attributeTypeMap);
        }

        private static IReadOnlyDictionary<string, Type> BuildAttributeTypeMap(Type entityType)
        {
            var result = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var logicalNameAttribute = property.GetCustomAttribute<AttributeLogicalNameAttribute>();
                if (logicalNameAttribute == null)
                {
                    continue;
                }

                var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

                if (typeof(Entity).IsAssignableFrom(propertyType) && propertyType != typeof(Entity))
                {
                    result[logicalNameAttribute.LogicalName] = typeof(EntityReference);
                }
                else
                {
                    result[logicalNameAttribute.LogicalName] = propertyType;
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
                    || (value is string dateString && DateTimeOffset.TryParse(dateString, out _));

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
                return value;
            }

            if (value is JsonElement jsonElement)
            {
                return ConvertJsonElementToExpectedType(jsonElement, normalizedExpected, attributeName);
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
                if (value is string dateString && DateTimeOffset.TryParse(dateString, out var parsedDto))
                    return parsedDto.UtcDateTime;
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
                        DateTimeOffset.TryParse(element.GetString(), out var dto))
                    {
                        return dto.UtcDateTime;
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