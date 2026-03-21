namespace enterprise_d365_gateway.Interfaces
{
    public interface IUpsertLockCoordinator
    {
        Task<IDisposable> AcquireAsync(string upsertKey, CancellationToken cancellationToken = default);
    }
}
