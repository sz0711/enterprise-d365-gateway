using System.Collections.Concurrent;
using enterprise_d365_gateway.Interfaces;

namespace enterprise_d365_gateway.Services
{
    public class UpsertLockCoordinator : IUpsertLockCoordinator
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

        public async Task<IDisposable> AcquireAsync(string keySignature, CancellationToken cancellationToken = default)
        {
            var normalizedKey = NormalizeKey(keySignature);
            var semaphore = _locks.GetOrAdd(normalizedKey, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(cancellationToken);
            return new LockRelease(semaphore);
        }

        private static string NormalizeKey(string key)
            => key.Trim().ToUpperInvariant();

        private sealed class LockRelease : IDisposable
        {
            private readonly SemaphoreSlim _semaphore;
            private int _disposed;

            public LockRelease(SemaphoreSlim semaphore) => _semaphore = semaphore;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    _semaphore.Release();
            }
        }
    }
}
