using Microsoft.Xrm.Sdk;
using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Interfaces
{
    public interface IEarlyboundEntityMapper
    {
        void ValidatePayload(UpsertPayload payload);
        Entity MapToEntity(UpsertPayload payload);

        /// <summary>
        /// Validates a lookup definition (entity known, attribute names defined,
        /// no restricted attributes) without throwing. Returns error strings
        /// prefixed with <paramref name="path"/>; empty when valid.
        /// </summary>
        IReadOnlyList<string> ValidateLookup(string path, LookupDefinition lookup);

        /// <summary>
        /// Converts a raw (possibly JsonElement) value into the primitive shape a
        /// QueryExpression condition needs for the given attribute
        /// (OptionSet → int, Money → decimal, EntityReference → Guid, typed
        /// primitives otherwise). Falls back to plain normalization when the
        /// attribute is unknown. Throws <see cref="PayloadValidationException"/>
        /// when the value cannot be converted.
        /// </summary>
        object? ConvertQueryValue(string entityLogicalName, string attributeName, object? value);

        /// <summary>
        /// Converts a raw (possibly JsonElement) value into the SDK attribute type
        /// used when writing the given attribute (OptionSetValue, Money, typed
        /// primitives). Falls back to plain normalization when the attribute is
        /// unknown. Throws <see cref="PayloadValidationException"/> when the value
        /// cannot be converted.
        /// </summary>
        object? ConvertWriteValue(string entityLogicalName, string attributeName, object? value);
    }
}
