using Chronicler.Projects;

namespace Chronicler.Tests;

internal sealed class FakeFileLockService : IFileLockService
{
    public List<string> Paths { get; } = [];
    public Func<string, Task<FileLockResult>> Query { get; set; } =
        _ => Task.FromResult(new FileLockResult(
            new Dictionary<string, IReadOnlyList<FileLockOwner>>(StringComparer.OrdinalIgnoreCase), null));

    public Task<FileLockResult> QueryAsync(string path)
    {
        Paths.Add(path);
        return Query(path);
    }
}
