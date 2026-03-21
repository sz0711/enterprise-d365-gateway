using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Tests.Helpers;

public class TestPayloadBuilder
{
    private string _entityLogicalName = "account";
    private string? _upsertKey = "EXT-001";
    private Guid? _id;
    private IDictionary<string, object?> _attributes = new Dictionary<string, object?> { ["name"] = "Test Account" };
    private string? _externalIdAttribute;
    private object? _externalIdValue;
    private IDictionary<string, LookupDefinition>? _lookups;
    private int? _maxLookupDepth;

    public TestPayloadBuilder WithEntity(string entityLogicalName) { _entityLogicalName = entityLogicalName; return this; }
    public TestPayloadBuilder WithUpsertKey(string? upsertKey) { _upsertKey = upsertKey; return this; }
    public TestPayloadBuilder WithId(Guid id) { _id = id; return this; }
    public TestPayloadBuilder WithAttributes(IDictionary<string, object?> attributes) { _attributes = attributes; return this; }
    public TestPayloadBuilder WithExternalId(string attribute, object value) { _externalIdAttribute = attribute; _externalIdValue = value; return this; }
    public TestPayloadBuilder WithLookup(string attributeName, LookupDefinition lookup)
    {
        _lookups ??= new Dictionary<string, LookupDefinition>();
        _lookups[attributeName] = lookup;
        return this;
    }
    public TestPayloadBuilder WithMaxLookupDepth(int depth) { _maxLookupDepth = depth; return this; }
    public TestPayloadBuilder WithNullAttributes() { _attributes = null!; return this; }

    public UpsertPayload Build() => new()
    {
        EntityLogicalName = _entityLogicalName,
        UpsertKey = _upsertKey,
        Id = _id,
        Attributes = _attributes,
        ExternalIdAttribute = _externalIdAttribute,
        ExternalIdValue = _externalIdValue,
        Lookups = _lookups,
        MaxLookupDepth = _maxLookupDepth
    };
}
