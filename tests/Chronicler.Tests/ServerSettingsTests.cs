using System.IO;
using System.Text.Json;
using Chronicler.Servers;

namespace Chronicler.Tests;

public sealed class ServerSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(directory, "servers.json");

    [Theory]
    [InlineData("Main", "lore://example.com:41337")]
    [InlineData("Local", "lore://localhost")]
    [InlineData("IPv6", "lore://[::1]:41337")]
    public void AcceptsServerAddresses(string name, string url)
    {
        Assert.True(ServerProfileValidator.TryValidate(name, url, out _));
    }

    [Theory]
    [InlineData("", "lore://localhost")]
    [InlineData("Name", "https://example.com")]
    [InlineData("Name", "lore://user:password@example.com")]
    [InlineData("Name", "lore://example.com/repository")]
    [InlineData("Name", "lore://example.com?token=secret")]
    [InlineData("Name", "lore://example.com#fragment")]
    [InlineData("Name", "lore://example.com:0")]
    [InlineData("Name", "lore://example.com:65536")]
    [InlineData("Name", "lore://example.com:abc")]
    public void RejectsInvalidOrSecretBearingAddresses(string name, string url)
    {
        Assert.False(ServerProfileValidator.TryValidate(name, url, out _));
    }

    [Fact]
    public void SavesAndReloadsSeveralProfilesWithoutCredentials()
    {
        var store = new ServerSettingsStore(SettingsPath);
        Assert.Empty(store.Load());
        var profiles = new[]
        {
            new ServerProfile(Guid.NewGuid(), "Main", "lore://example.com:41337"),
            new ServerProfile(Guid.NewGuid(), "Test", "lore://localhost:41337")
        };
        store.Save(profiles);

        Assert.Equal(profiles, new ServerSettingsStore(SettingsPath).Load());
        var json = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);

        store.Save([profiles[1]]);
        Assert.Equal([profiles[1]], store.Load());
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void InvalidInputDoesNotReplaceExistingFile()
    {
        var store = new ServerSettingsStore(SettingsPath);
        var original = new ServerProfile(Guid.NewGuid(), "Main", "lore://localhost");
        store.Load();
        store.Save([original]);
        var duplicate = new ServerProfile(Guid.NewGuid(), "main", "lore://example.com");

        Assert.Throws<InvalidDataException>(() => store.Save([original, duplicate]));
        Assert.Equal([original], store.Load());
    }

    [Fact]
    public void DoesNotOverwriteChangesMadeByAnotherInstance()
    {
        var first = new ServerSettingsStore(SettingsPath);
        var second = new ServerSettingsStore(SettingsPath);
        first.Load();
        second.Load();
        var original = new ServerProfile(Guid.NewGuid(), "First", "lore://localhost");
        first.Save([original]);

        var conflict = new ServerProfile(Guid.NewGuid(), "Second", "lore://example.com");
        Assert.Throws<InvalidOperationException>(() => second.Save([conflict]));
        Assert.Equal([original], first.Load());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"Version\":2,\"Servers\":[]}")]
    [InlineData("{\"Version\":1,\"Servers\":[null]}")]
    [InlineData("{\"Version\":1,\"Servers\":[{\"Id\":\"00000000-0000-0000-0000-000000000000\",\"Name\":\"x\",\"RemoteUrl\":\"lore://localhost\"}]}")]
    public void InvalidSettingsAreReportedAndLeftUntouched(string contents)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, contents);
        var store = new ServerSettingsStore(SettingsPath);
        var exception = Record.Exception(() => store.Load());
        Assert.True(exception is JsonException or InvalidDataException);
        Assert.Equal(contents, File.ReadAllText(SettingsPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
