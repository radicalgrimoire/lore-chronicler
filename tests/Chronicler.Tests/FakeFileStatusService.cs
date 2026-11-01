using Chronicler.Projects;

namespace Chronicler.Tests;

internal sealed class FakeFileStatusService : IFileStatusService
{
    public List<string> Paths { get; } = [];
    public Func<string, Task<FileStatusSnapshot>> Scan { get; set; } =
        path => Task.FromResult(new FileStatusSnapshot(path, new Dictionary<string, FileChangeKind>()));

    public Task<FileStatusSnapshot> ScanAsync(string path)
    {
        Paths.Add(path);
        return Scan(path);
    }
}
