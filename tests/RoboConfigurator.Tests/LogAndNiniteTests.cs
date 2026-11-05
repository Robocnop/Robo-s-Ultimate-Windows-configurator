using RoboConfigurator.Core;
using Xunit;

namespace RoboConfigurator.Tests
{
    public class LogTests
    {
        [Theory]
        [InlineData("slmgr /ipk VK7JG-NPHTM-C97JM-9MPGT-3V66T", "slmgr /ipk XXXXX-XXXXX-XXXXX-XXXXX-3V66T")]
        [InlineData("clé vk7jg-nphtm-c97jm-9mpgt-3v66t ok", "clé XXXXX-XXXXX-XXXXX-XXXXX-3v66t ok")]
        [InlineData("AAAAA-BBBBB-CCCCC-DDDDD-EEEEE et FFFFF-GGGGG-HHHHH-IIIII-JJJJJ", "XXXXX-XXXXX-XXXXX-XXXXX-EEEEE et XXXXX-XXXXX-XXXXX-XXXXX-JJJJJ")]
        [InlineData("Rien à masquer : 12345-678", "Rien à masquer : 12345-678")]
        [InlineData(null, "")]
        public void MaskKeys(string text, string expected) => Assert.Equal(expected, Log.MaskKeys(text));
    }

    public class NiniteAppsTests
    {
        [Fact]
        public void Normalize_DropsUnknownAndDuplicates_CatalogOrder() =>
            Assert.Equal(new[] { "firefox", "vlc" }, NiniteApps.Normalize(new[] { "VLC", "inconnue", "firefox", "vlc" }));

        [Fact]
        public void IsDefault_IgnoresOrder() => Assert.True(NiniteApps.IsDefault(new[] { "notepadplusplus", "foxit", "firefox", "chrome", "7zip" }));

        [Fact]
        public void FileName_StableAndPerSelection()
        {
            Assert.Equal(NiniteApps.FileName(new[] { "vlc", "7zip" }), NiniteApps.FileName(new[] { "7zip", "vlc" }));
            Assert.NotEqual(NiniteApps.FileName(new[] { "vlc" }), NiniteApps.FileName(new[] { "7zip" }));
            Assert.Matches("^Ninite-[0-9a-f]{8}\\.exe$", NiniteApps.FileName(new[] { "vlc" }));
        }

        [Fact]
        public void Url() => Assert.Equal("https://ninite.com/vlc-7zip/ninite.exe", NiniteApps.Url(new[] { "vlc", "7zip" }));
    }
}
