using System.IO;
using Chronicler.Projects;

namespace Chronicler.Tests;

internal sealed class FakeProjectRepositoryInfo : IProjectRepositoryInfo
{
    public string RemoteUrl { get; set; } = "lore://localhost:41337";
    public string RepositoryId { get; set; } = "1234567890ABCDEF1234567890ABCDEF";
    public string? Name { get; set; }
    public string? ServerRepositoryId { get; set; }
    public Exception? RemoteError { get; set; }
    public Exception? LocalError { get; set; }
    public int RemoteCalls { get; private set; }
    public Func<string, Task<string>>? ReadServerUrl { get; set; }

    public Task<string> ReadServerUrlAsync(string path) =>
        ReadServerUrl?.Invoke(path) ?? Task.FromResult(RemoteUrl);

    public Task<LocalRepositoryInfo> ReadLocalAsync(string path)
    {
        if (LocalError is not null)
        {
            throw LocalError;
        }
        if (!Directory.Exists(Path.Combine(path, ".lore")))
        {
            throw new DirectoryNotFoundException(path);
        }
        return Task.FromResult(new LocalRepositoryInfo(RemoteUrl, RepositoryId));
    }

    public Task<RemoteRepositoryInfo> ReadRemoteAsync(string path)
    {
        RemoteCalls++;
        if (RemoteError is not null)
        {
            throw RemoteError;
        }
        var name = Name ?? Path.GetFileName(path) switch
        {
            "first" => "repo1",
            "second" => "repo2",
            _ => "repo"
        };
        return Task.FromResult(new RemoteRepositoryInfo(RemoteUrl, ServerRepositoryId ?? RepositoryId, name));
    }
}
