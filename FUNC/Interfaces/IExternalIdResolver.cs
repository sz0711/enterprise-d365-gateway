namespace enterprise_d365_gateway.Interfaces
{
    public interface IExternalIdResolver
    {
        Task<Guid?> ResolveAsync(
            string entityLogicalName,
            string externalIdAttribute,
            object externalIdValue,
            CancellationToken cancellationToken = default);

        void Invalidate(string entityLogicalName, string externalIdAttribute, string externalIdValue);
    }
}
