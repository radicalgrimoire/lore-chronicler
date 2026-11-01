using System.IO;
using Chronicler.Projects;
using LoreVcs;
using LoreVcs.Types.Args;

namespace Chronicler.Tests;

public sealed class FileLockServiceTests
{
    [Fact]
    public async Task QueryWithoutRemoteReportsFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            using var globals = new LoreGlobalArgs { RepositoryPath = path, Offline = true };
            using var create = new LoreRepositoryCreateArgs { RepositoryUrl = $"lock-{Guid.NewGuid():N}" };
            await Lore.RepositoryCreate(globals, create).WaitAsync();
            await Assert.ThrowsAsync<LoreError>(() => new LoreFileLockService().QueryAsync(path));
        }
        finally
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(path, recursive: true);
                    break;
                }
                catch (IOException) when (attempt < 20)
                {
                    await Task.Delay(100);
                }
            }
        }
    }
}
