using RoboConfigurator.Core;
using Xunit;

namespace RoboConfigurator.Tests
{
    public class UpdatesTests
    {
        [Theory]
        [InlineData("2.6.3", "2.6.2", true)]
        [InlineData("2.6.10", "2.6.9", true)]
        [InlineData("3.0.0", "2.99.99", true)]
        [InlineData("2.6.0", "2.6.0-beta.1", true)]
        [InlineData("2.6.2", "2.6.2", false)]
        [InlineData("2.5.0", "2.6.0", false)]
        [InlineData("2.6.0-beta.1", "2.6.0", false)]
        [InlineData("2.7.0-beta.1", "2.6.0", true)]
        [InlineData("2.6.0-beta.2", "2.6.0-beta.1", false)]
        [InlineData("2.6.1", "2.6", true)]
        [InlineData("", "2.6.0", false)]
        [InlineData("abc", "2.6.0", false)]
        [InlineData("2.6.0", null, false)]
        public void IsNewer(string candidate, string current, bool expected) => Assert.Equal(expected, Updates.IsNewer(candidate, current));

        [Theory]
        [InlineData("https://github.com/Robocnop/Robo-s-Ultimate-Windows-configurator/releases/download/v2.6.2/RoboConfigurator.exe", true)]
        [InlineData("https://GitHub.com/Robocnop/x/releases/tag/v2.6.2", true)]
        [InlineData("http://github.com/x", false)]
        [InlineData("https://github.com.evil.example/x", false)]
        [InlineData(@"C:\Windows\System32\cmd.exe", false)]
        [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsTrustedUrl(string url, bool expected) => Assert.Equal(expected, Updates.IsTrustedUrl(url));
    }
}
