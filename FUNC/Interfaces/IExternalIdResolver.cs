namespace enterprise_d365_gateway.Interfaces
{
    public interface IExternalIdResolver
    {
        /// <param name="keySignature">
        /// Optional precomputed signature (KeyAttributesFormatter format) to avoid
        /// rebuilding it when the caller already has one.
        /// </param>
        Task<Guid?> ResolveAsync(
            string entityLogicalName,
            IDictionary<string, object?> keyAttributes,
            CancellationToken cancellationToken = default,
            string? keySignature = null);

        void Invalidate(string entityLogicalName, IDictionary<string, object?> keyAttributes);
    }
}
