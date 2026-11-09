using System.IO;

namespace Chronicler.Projects;

internal static class LoreRepositoryOperationGate
{
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Sync = new();

    public static async Task<IDisposable> AcquireAsync(string path)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        Entry entry;
        lock (Sync)
        {
            if (!Entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                Entries.Add(key, entry);
            }
            entry.Users++;
        }

        await entry.Semaphore.WaitAsync();
        return new Lease(key, entry);
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;
    }

    private sealed class Lease(string key, Entry entry) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            lock (Sync)
            {
                if (disposed) return;
                disposed = true;
                entry.Semaphore.Release();
                if (--entry.Users == 0)
                {
                    Entries.Remove(key);
                    entry.Semaphore.Dispose();
                }
            }
        }
    }
}
