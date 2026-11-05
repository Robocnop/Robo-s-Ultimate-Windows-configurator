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

        [Fact]
        public void UnwritableLogFile_NeverThrows()
        {
            using (var dir = new TempDir())
            {
                // Un dossier à la place du fichier : l'écriture lève UnauthorizedAccessException
                string path = System.IO.Directory.CreateDirectory(dir.File("journal.txt")).FullName;
                var seen = new System.Collections.Generic.List<string>();
                System.Action<LogEntry> handler = e => seen.Add(e.Text);
                Log.Written += handler;
                try
                {
                    Log.Open(path);
                    Log.Info("un");
                    Log.Info("deux");
                }
                finally { Log.Written -= handler; Log.Close(); }
                Assert.Contains("deux", seen);
            }
        }
    }

    public class StepNamesTests
    {
        [Fact]
        public void Display_TranslatesTheInternalNames()
        {
            try
            {
                Lang.Set("fr");
                Assert.Equal("Barre des tâches, Activation de Windows", StepNames.Display(new[] { StepNames.Taskbar, StepNames.Activation }));
                Lang.Set("en");
                Assert.Equal("Taskbar, Windows activation", StepNames.Display(new[] { StepNames.Taskbar, StepNames.Activation }));
                Assert.Equal("Ninite", StepNames.Display(StepNames.Ninite));
            }
            finally { Lang.Set("en"); }
        }
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
