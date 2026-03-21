using Microsoft.Xrm.Sdk;
using enterprise_d365_gateway.Models;

namespace enterprise_d365_gateway.Interfaces
{
    public interface ILookupResolver
    {
        Task<(EntityReference Reference, LookupTrace Trace)> ResolveAsync(
            string attributeName,
            LookupDefinition lookupDef,
            int currentDepth,
            int maxDepth,
            HashSet<string> visited,
            CancellationToken cancellationToken = default);
    }
}
