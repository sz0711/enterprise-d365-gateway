using System.Net;
using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Interfaces
{
    public interface IResultMapper
    {
        UpsertResult MapSuccess(string entityLogicalName, string? keySignature, Guid id, bool created, IList<LookupTrace>? lookupTraces = null);
        UpsertResult MapError(string entityLogicalName, string? keySignature, Exception exception, ErrorCategory category);
        HttpStatusCode DetermineBatchStatusCode(IReadOnlyList<UpsertResult> results);
    }
}
