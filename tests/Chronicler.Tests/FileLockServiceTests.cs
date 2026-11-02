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
            var service = new LoreFileLockService();
            await Assert.ThrowsAsync<LoreError>(() => service.QueryAsync(path));
            await Assert.ThrowsAsync<LoreError>(() => service.QueryMineAsync(path));
            var file = Path.Combine(path, "nested", "asset.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "asset");
            var workingDirectory = Environment.CurrentDirectory;
            await Assert.ThrowsAsync<LoreError>(() => service.AcquireAsync(path, file));
            Assert.Equal(workingDirectory, Environment.CurrentDirectory);
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

    [Theory]
    [InlineData("..\\outside.txt")]
    [InlineData(".lore\\config")]
    [InlineData(".")]
    public async Task RejectsUnsafeLockTargetsBeforeCallingSdk(string target)
    {
        var path = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
        var service = new LoreFileLockService();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.AcquireAsync(path, Path.GetFullPath(Path.Combine(path, target))));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReleaseAsync(path,
            new MyFileLock(Path.GetFullPath(Path.Combine(path, target)), target, "branch", "owner")));
    }

    [Fact]
    public async Task RejectsDirectoryLockBeforeCallingSdk()
    {
        var path = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(path, "folder");
        Directory.CreateDirectory(folder);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new LoreFileLockService().AcquireAsync(path, folder));
        }
        finally
        {
            Directory.Delete(path, true);
        }
    }
}
