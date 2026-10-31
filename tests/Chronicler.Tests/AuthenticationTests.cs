using Chronicler.Authentication;
using LoreVcs;

namespace Chronicler.Tests;

public class AuthenticationTests
{
    [Theory]
    [InlineData("https://auth.example.com/login", true)]
    [InlineData("https://auth.example.com/login?state=abc", true)]
    [InlineData("http://localhost:1234/login", true)]
    [InlineData("http://127.0.0.1:1234/login", true)]
    [InlineData("http://auth.example.com/login", false)]
    [InlineData("https://user:password@auth.example.com/login", false)]
    [InlineData("file:///C:/test", false)]
    [InlineData("not a URL", false)]
    public void ValidatesBrowserDestinations(string url, bool expected)
    {
        Assert.Equal(expected, LoginBrowser.IsSafeUrl(url));
    }

    [Fact]
    public async Task SdkReportsInvalidRemoteWithoutOpeningBrowser()
    {
        var service = new LoreLoginService();
        var progress = new Progress<LoginProgress>();
        await Assert.ThrowsAsync<LoreError>(() => service.LoginAsync("not a valid remote", progress));
    }
}
