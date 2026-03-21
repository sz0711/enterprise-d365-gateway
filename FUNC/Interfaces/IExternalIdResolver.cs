namespace enterprise_d365_gateway.Interfaces
{
    public interface IExternalIdResolver
    {
        Task<Guid?> ResolveAsync(
            string entityLogicalName,
            IDictionary<string, object?> keyAttributes,
            CancellationToken cancellationToken = default);

        void Invalidate(string entityLogicalName, IDictionary<string, object?> keyAttributes);
    }
}
