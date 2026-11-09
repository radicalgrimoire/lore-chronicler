using Chronicler.Projects;

namespace Chronicler.Tests;

internal sealed class FakeBranchListService : IBranchService
{
    public Task<BranchListResult> ListAsync(string path) =>
        Task.FromResult(new BranchListResult([new BranchEntry("1", "main", true, false, true)], false));

    public Task SwitchAsync(string path, BranchEntry branch) =>
        throw new InvalidOperationException("Unexpected branch switch");
}
