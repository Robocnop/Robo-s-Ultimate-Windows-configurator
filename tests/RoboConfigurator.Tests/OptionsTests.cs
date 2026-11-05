using System;
using RoboConfigurator.Core;
using Xunit;

namespace RoboConfigurator.Tests
{
    public class OptionsTests : IDisposable
    {
        public void Dispose() => Lang.Set("en");

        [Fact]
        public void NoArguments_Defaults()
        {
            var o = Options.Parse(new string[0]);
            Assert.Empty(o.Errors);
            Assert.Empty(o.Skip);
            Assert.False(o.AllTweaks || o.Reset || o.Simulation || o.ForceCleanUp);
            Assert.Equal(Editions.Keep, o.Edition);
            Assert.Equal("", o.WindowsKey);
            Assert.Equal(NiniteApps.Default, o.Apps);
        }

        [Fact]
        public void AllExistingOptions()
        {
            var o = Options.Parse(new[] { "-Skip", "office, taskbar", "-Apps", "VLC,7zip", "-AllTweaks", "-WindowsKey", "aaaaa-bbbbb-ccccc-ddddd-eeeee",
                                          "-Edition", "Pro", "-Lang", "fr", "-Simulation", "-Reset", "-ForceCleanUp" });
            Assert.Empty(o.Errors);
            Assert.Equal(new[] { StepNames.Office, StepNames.Taskbar }, o.Skip);
            Assert.Equal(new[] { "vlc", "7zip" }, o.Apps);
            Assert.True(o.AllTweaks && o.Simulation && o.Reset && o.ForceCleanUp);
            Assert.Equal("AAAAA-BBBBB-CCCCC-DDDDD-EEEEE", o.WindowsKey);
            Assert.Equal(Editions.Pro, o.Edition);
            Assert.True(Lang.French);
        }

        [Theory]
        [InlineData("-simulation")]
        [InlineData("/Simulation")]
        [InlineData("--SIMULATION")]
        public void PrefixesAndCase(string arg) => Assert.True(Options.Parse(new[] { arg }).Simulation);

        [Theory]
        [InlineData("home", Editions.Home)]
        [InlineData("Famille", Editions.Home)]
        [InlineData("core", Editions.Home)]
        [InlineData("professional", Editions.Pro)]
        [InlineData("keep", Editions.Keep)]
        public void EditionAliases(string value, string expected) => Assert.Equal(expected, Options.Parse(new[] { "-Edition", value }).Edition);

        [Fact]
        public void UnknownValuesAreReported()
        {
            var o = Options.Parse(new[] { "-Skip", "Office,Nope", "-Edition", "Ultimate", "-Lang", "de", "-Inconnue" });
            Assert.Equal(4, o.Errors.Count);
            Assert.Equal(new[] { StepNames.Office }, o.Skip);
            Assert.Equal(Editions.Keep, o.Edition);
        }

        [Fact]
        public void MissingValueAtTheEnd()
        {
            var o = Options.Parse(new[] { "-Skip" });
            Assert.Empty(o.Skip);
            Assert.Empty(o.Errors);
        }

        [Fact]
        public void UnknownAppIsDroppedAndReported()
        {
            var o = Options.Parse(new[] { "-Apps", "vlc,inconnue" });
            Assert.Equal(new[] { "vlc" }, o.Apps);
            Assert.Single(o.Errors);
        }
    }
}
