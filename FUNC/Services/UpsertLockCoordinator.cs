using System.Collections.Concurrent;
using enterprise_d365_gateway.Interfaces;

namespace enterprise_d365_gateway.Services
{
    /// <summary>
    /// Keyed async lock with reference counting: semaphores are removed from the
    /// dictionary (and disposed) as soon as the last holder/waiter releases, so
    /// memory no longer grows with the number of distinct key signatures.
    /// </summary>
    public class UpsertLockCoordinator : IUpsertLockCoordinator
    {
        private readonly ConcurrentDictionary<string, LockEntry> _locks = new(StringComparer.Ordinal);

        public async Task<IDisposable> AcquireAsync(string keySignature, CancellationToken cancellationToken = default)
        {
            var normalizedKey = NormalizeKey(keySignature);

            while (true)
            {
                var entry = _locks.GetOrAdd(normalizedKey, _ => new LockEntry());

                // Register interest under the entry's own lock; an entry that is
                // already marked removed belongs to a finished lifecycle — retry.
                lock (entry)
                {
                    if (entry.Removed)
                        continue;
                    entry.RefCount++;
                }

                try
                {
                    await entry.Semaphore.WaitAsync(cancellationToken);
                }
                catch
                {
                    ReleaseRef(normalizedKey, entry);
                    throw;
                }

                return new LockRelease(this, normalizedKey, entry);
            }
        }

        private void ReleaseRef(string key, LockEntry entry)
        {
            bool dispose = false;
            lock (entry)
            {
                entry.RefCount--;
                if (entry.RefCount == 0)
                {
                    entry.Removed = true;
                    dispose = true;
                }
            }

            if (dispose)
            {
                _locks.TryRemove(new KeyValuePair<string, LockEntry>(key, entry));
                entry.Semaphore.Dispose();
            }
        }

        private static string NormalizeKey(string key)
            => key.Trim().ToUpperInvariant();

        private sealed class LockEntry
        {
            public readonly SemaphoreSlim Semaphore = new(1, 1);
            public int RefCount;
            public bool Removed;
        }

        private sealed class LockRelease : IDisposable
        {
            private readonly UpsertLockCoordinator _owner;
            private readonly string _key;
            private readonly LockEntry _entry;
            private int _disposed;

            public LockRelease(UpsertLockCoordinator owner, string key, LockEntry entry)
            {
                _owner = owner;
                _key = key;
                _entry = entry;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _entry.Semaphore.Release();
                    _owner.ReleaseRef(_key, _entry);
                }
            }
        }
    }
}
