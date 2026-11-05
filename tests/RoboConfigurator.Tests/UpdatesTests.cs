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
    }
}
