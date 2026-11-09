using System.IO;
using Chronicler.Projects;

namespace Chronicler.Tests;

public sealed class LoreRepositoryOperationGateTests
{
    [Fact]
    public async Task SerializesTheSameRepositoryWithoutBlockingOthers()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var first = await LoreRepositoryOperationGate.AcquireAsync(path);
        var secondTask = LoreRepositoryOperationGate.AcquireAsync(path + Path.DirectorySeparatorChar);
        Assert.False(secondTask.IsCompleted);

        using (await LoreRepositoryOperationGate.AcquireAsync(path + "-other").WaitAsync(TimeSpan.FromSeconds(5)))
        {
            Assert.False(secondTask.IsCompleted);
        }

        first.Dispose();
        using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        var thirdTask = LoreRepositoryOperationGate.AcquireAsync(path);
        Assert.False(thirdTask.IsCompleted);
        second.Dispose();
        using var third = await thirdTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
