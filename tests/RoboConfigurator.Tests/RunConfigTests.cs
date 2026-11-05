using System;
using System.IO;
using RoboConfigurator.Core;
using Xunit;

namespace RoboConfigurator.Tests
{
    public sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RoboTests-" + Guid.NewGuid().ToString("N"));
        public TempDir() { Directory.CreateDirectory(Path); }
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (Exception) { } }
    }

    public class RunConfigTests
    {
        [Fact]
        public void SaveThenLoad_RoundTrip()
        {
            using (var dir = new TempDir())
            {
                var config = new RunConfig
                {
                    NewName = "POSTE7", Steps = new[] { StepNames.Edge, StepNames.Ninite }, AllTweaks = true,
                    WindowsKey = "AAAAA-BBBBB-CCCCC-DDDDD-EEEEE", AutoRestart = false, CleanUp = false,
                    CleanUpOnError = true, Edition = Editions.Pro, NiniteApps = new[] { "vlc", "7zip" },
                };
                config.Save(dir.File("c.json"));
                var loaded = RunConfig.Load(dir.File("c.json"));
                Assert.Equal("POSTE7", loaded.NewName);
                // Remises dans l'ordre des étapes
                Assert.Equal(new[] { StepNames.Ninite, StepNames.Edge }, loaded.Steps);
                Assert.True(loaded.AllTweaks);
                Assert.Equal("AAAAA-BBBBB-CCCCC-DDDDD-EEEEE", loaded.WindowsKey);
                Assert.False(loaded.AutoRestart);
                Assert.False(loaded.CleanUp);
                Assert.True(loaded.CleanUpOnError);
                Assert.Equal(Editions.Pro, loaded.Edition);
                Assert.Equal(new[] { "vlc", "7zip" }, loaded.NiniteApps);
            }
        }

        [Fact]
        public void Load_ConfigFromBeforeV260_UsesDefaultApps()
        {
            using (var dir = new TempDir())
            {
                File.WriteAllText(dir.File("c.json"), "{\"NewName\":\"PC1\",\"Steps\":[\"Ninite\",\"Inconnue\"],\"Edition\":\"famille\"}");
                var loaded = RunConfig.Load(dir.File("c.json"));
                Assert.Equal(NiniteApps.Default, loaded.NiniteApps);
                Assert.Equal(new[] { StepNames.Ninite }, loaded.Steps);
                Assert.Equal(Editions.Home, loaded.Edition);
                Assert.Equal("", loaded.WindowsKey);
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("{\"NewName\":")]
        public void Load_CorruptFileThrows(string content)
        {
            using (var dir = new TempDir())
            {
                File.WriteAllText(dir.File("c.json"), content);
                Assert.ThrowsAny<Exception>(() => RunConfig.Load(dir.File("c.json")));
            }
        }

        [Theory]
        [InlineData("POSTE6", true)]
        [InlineData("A", true)]
        [InlineData("PC-COMPTA-01", true)]
        [InlineData("123456789012345", false)]
        [InlineData("ABCDEFGHIJKLMNOP", false)]
        [InlineData("PC_1", false)]
        [InlineData("PC 1", false)]
        [InlineData("", false)]
        public void IsValidPcName(string name, bool expected) => Assert.Equal(expected, RunConfig.IsValidPcName(name));

        [Theory]
        [InlineData("VK7JG-NPHTM-C97JM-9MPGT-3V66T", true)]
        [InlineData("VK7JGNPHTMC97JM9MPGT3V66T", false)]
        [InlineData("VK7JG-NPHTM-C97JM-9MPGT", false)]
        [InlineData("vk7jg-nphtm-c97jm-9mpgt-3v66t", false)]
        public void IsValidKey(string key, bool expected) => Assert.Equal(expected, RunConfig.IsValidKey(key));

        [Fact]
        public void StepState_MarkDoneThenIsDone()
        {
            using (var dir = new TempDir())
            {
                var state = new StepState(dir.File("s.txt"));
                Assert.False(state.IsDone(StepNames.Edge));
                state.MarkDone(StepNames.Edge);
                Assert.True(state.IsDone(StepNames.Edge));
                Assert.False(state.IsDone(StepNames.Office));
            }
        }
    }
}
