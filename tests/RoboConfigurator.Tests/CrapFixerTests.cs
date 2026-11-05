using System;
using System.Linq;
using RoboConfigurator.Core;
using Xunit;

namespace RoboConfigurator.Tests
{
    public class CrapFixerTests
    {
        private static readonly string[] Sample =
        {
            "; commentaire en tête",
            "Orphan=ignorée",
            "",
            "[Disable Telemetry]",
            @"Path=HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
            "ValueName=AllowTelemetry",
            "RecommendedValue=0",
            "DefaultSelected=true   ; recommended",
            "",
            "[Enable Dark Theme]",
            @"path=HKCU\Software\Test",
            "DefaultSelected=false",
            "",
            "[Win11 Only]",
            @"Path=HKCU\Software\Test",
            "OsVersion=11",
            "DefaultSelected=TRUE",
            "",
            "[Optional]",
            @"Path=HKCU\Software\Test",
            "Formula=a=b",
        };

        [Fact]
        public void ReadIni_ReadsSectionsAndValues()
        {
            var ini = CrapFixer.ReadIni(Sample);
            Assert.Equal(new[] { "Disable Telemetry", "Enable Dark Theme", "Win11 Only", "Optional" }, ini.Select(s => s.Name));
            Assert.Equal("AllowTelemetry", ini[0]["ValueName"]);
            Assert.Null(ini[0]["Orphan"]);
        }

        [Fact]
        public void ReadIni_StripsEndOfLineComment() => Assert.True(CrapFixer.ReadIni(Sample)[0].DefaultSelected);

        [Fact]
        public void ReadIni_KeysIgnoreCase() => Assert.True(CrapFixer.ReadIni(Sample)[1].Has("Path"));

        [Fact]
        public void ReadIni_KeepsEqualsInValue() => Assert.Equal("a=b", CrapFixer.ReadIni(Sample)[3]["Formula"]);

        [Fact]
        public void ReadIni_DefaultSelectedIgnoresCase() => Assert.True(CrapFixer.ReadIni(Sample)[2].DefaultSelected);

        [Fact]
        public void SelectTweaks_RecommendedPlusDarkTheme_Win10()
        {
            var names = CrapFixer.SelectTweaks(CrapFixer.ReadIni(Sample), false, isWin11: false).Select(t => t.Name);
            Assert.Equal(new[] { "Disable Telemetry", "Enable Dark Theme" }, names);
        }

        [Fact]
        public void SelectTweaks_Win11OnlyKeptOnWin11() =>
            Assert.Contains("Win11 Only", CrapFixer.SelectTweaks(CrapFixer.ReadIni(Sample), false, isWin11: true).Select(t => t.Name));

        [Fact]
        public void SelectTweaks_AllTweaks()
        {
            var names = CrapFixer.SelectTweaks(CrapFixer.ReadIni(Sample), true, isWin11: false).Select(t => t.Name).ToList();
            Assert.Contains("Optional", names);
            Assert.DoesNotContain("Win11 Only", names);
        }

        [Theory]
        [InlineData("0", 0UL)]
        [InlineData(" 42 ", 42UL)]
        [InlineData("0x10", 16UL)]
        [InlineData("0XFFFFFFFF", 0xFFFFFFFFUL)]
        public void ParseNumber_DecimalAndHex(string value, ulong expected) => Assert.Equal(expected, CrapFixer.ParseNumber(value));

        [Fact]
        public void ParseNumber_InvalidThrows() => Assert.ThrowsAny<FormatException>(() => CrapFixer.ParseNumber("abc"));

        [Fact]
        public void ParseProvisioned_KeepsValidLines()
        {
            var list = CrapFixer.ParseProvisioned(new[]
            {
                "Microsoft.BingNews|Microsoft.BingNews_4.55.62231.0_neutral_~_8wekyb3d8bbwe",
                "",
                "sans séparateur",
                "|vide",
                " Clipchamp.Clipchamp | Clipchamp.Clipchamp_3.0.10220.0_neutral_~_yxz26nhyzhsrt ",
            });
            Assert.Equal(2, list.Count);
            Assert.Equal("Clipchamp.Clipchamp", list[1].Key);
            Assert.Equal("Clipchamp.Clipchamp_3.0.10220.0_neutral_~_yxz26nhyzhsrt", list[1].Value);
            Assert.True(CrapFixer.MatchesPackage("Microsoft.BingNews", list[0].Key));
        }

        [Fact]
        public void ApplyTweak_RefusesToDeleteAWholeHive()
        {
            // HKCU est de toute façon protégée par .NET : le test ne peut rien supprimer, même si le contrôle disparaissait
            var tweak = CrapFixer.ReadIni(new[] { "[Bad]", @"Path=HKCU\", "RecommendedValue=<delete>" })[0];
            var ex = Assert.ThrowsAny<Exception>(() => CrapFixer.ApplyTweak(tweak));
            Assert.Contains("HKCU", ex.Message);
        }

        [Theory]
        [InlineData("Microsoft.BingNews", "Microsoft.BingNews", true)]
        [InlineData("microsoft.bingnews", "Microsoft.BingNews", true)]
        [InlineData("*CandyCrush*", "king.com.CandyCrushSaga", true)]
        [InlineData("Microsoft.Bing?eather", "Microsoft.BingWeather", true)]
        [InlineData("Microsoft.Bing", "Microsoft.BingNews", false)]
        [InlineData("Microsoft.BingNews", "MicrosoftXBingNews", false)]
        public void MatchesPackage_Wildcards(string pattern, string name, bool expected) =>
            Assert.Equal(expected, CrapFixer.MatchesPackage(pattern, name));
    }
}
