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

        [Fact]
        public void Save_NeverWritesTheKeyInClear()
        {
            using (var dir = new TempDir())
            {
                new RunConfig { WindowsKey = "VK7JG-NPHTM-C97JM-9MPGT-3V66T" }.Save(dir.File("c.json"));
                string raw = File.ReadAllText(dir.File("c.json"));
                Assert.DoesNotContain("VK7JG", raw);
                Assert.DoesNotContain("3V66T", raw);
                Assert.Contains("ProtectedWindowsKey", raw);
                Assert.Equal("VK7JG-NPHTM-C97JM-9MPGT-3V66T", RunConfig.Load(dir.File("c.json")).WindowsKey);
            }
        }

        [Fact]
        public void Save_WithoutKey_WritesNoKeyField()
        {
            using (var dir = new TempDir())
            {
                new RunConfig().Save(dir.File("c.json"));
                Assert.DoesNotContain("WindowsKey", File.ReadAllText(dir.File("c.json")));
                Assert.Equal("", RunConfig.Load(dir.File("c.json")).WindowsKey);
            }
        }

        [Fact]
        public void Load_PlainKeyFromAnOlderVersion_IsReadThenEncryptedOnSave()
        {
            using (var dir = new TempDir())
            {
                File.WriteAllText(dir.File("c.json"), "{\"WindowsKey\":\"VK7JG-NPHTM-C97JM-9MPGT-3V66T\"}");
                var config = RunConfig.Load(dir.File("c.json"));
                Assert.Equal("VK7JG-NPHTM-C97JM-9MPGT-3V66T", config.WindowsKey);
                config.Save(dir.File("c.json"));
                Assert.DoesNotContain("VK7JG", File.ReadAllText(dir.File("c.json")));
            }
        }

        [Fact]
        public void Load_UndecryptableKey_GivesAnEmptyKey()
        {
            using (var dir = new TempDir())
            {
                File.WriteAllText(dir.File("c.json"), "{\"ProtectedWindowsKey\":\"AAAA\"}");
                Assert.Equal("", RunConfig.Load(dir.File("c.json")).WindowsKey);
            }
        }

        [Fact]
        public void Save_ReplacesTheFileAndLeavesNoTemporaryFile()
        {
            using (var dir = new TempDir())
            {
                new RunConfig { NewName = "AVANT" }.Save(dir.File("c.json"));
                new RunConfig { NewName = "APRES" }.Save(dir.File("c.json"));
                Assert.Equal("APRES", RunConfig.Load(dir.File("c.json")).NewName);
                Assert.False(File.Exists(dir.File("c.json.tmp")));
            }
        }

        [Fact]
        public void SetAsideUnreadable_KeepsTheDamagedFile()
        {
            using (var dir = new TempDir())
            {
                File.WriteAllText(dir.File("c.json"), "{\"NewName\":");
                string aside = RunConfig.SetAsideUnreadable(dir.File("c.json"));
                Assert.False(File.Exists(dir.File("c.json")));
                Assert.Equal("{\"NewName\":", File.ReadAllText(aside));
            }
        }

        [Fact]
        public void StepState_IgnoresAPartialLine()
        {
            using (var dir = new TempDir())
            {
                File.WriteAllText(dir.File("s.txt"), "Ninite\r\nEdge \r\nOffi");
                var state = new StepState(dir.File("s.txt"));
                Assert.True(state.IsDone(StepNames.Ninite));
                Assert.True(state.IsDone(StepNames.Edge));
                Assert.False(state.IsDone(StepNames.Office));
                state.MarkDone(StepNames.Office);
                Assert.True(state.IsDone(StepNames.Office));
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
