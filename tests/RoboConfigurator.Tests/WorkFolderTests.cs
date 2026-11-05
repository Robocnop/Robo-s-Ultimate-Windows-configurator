using System.Diagnostics;
using System.IO;
using RoboConfigurator.Core;
using Xunit;

namespace RoboConfigurator.Tests
{
    public class WorkFolderTests
    {
        private static void Junction(string link, string target)
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false };
            using (var p = Process.Start(psi)) p.WaitForExit();
            Assert.True(Directory.Exists(link), "mklink /J a échoué");
        }

        [Fact]
        public void Delete_RemovesEverythingInside()
        {
            using (var dir = new TempDir())
            {
                string work = Directory.CreateDirectory(dir.File("work")).FullName;
                Directory.CreateDirectory(Path.Combine(work, @"CrapFixer\sub"));
                File.WriteAllText(Path.Combine(work, "a.txt"), "a");
                File.WriteAllText(Path.Combine(work, @"CrapFixer\sub\b.txt"), "b");
                File.SetAttributes(Path.Combine(work, "a.txt"), FileAttributes.ReadOnly);

                Assert.Equal(0, WorkFolder.Delete(work));
                Assert.False(Directory.Exists(work));
            }
        }

        [Fact]
        public void Delete_RemovesJunctionButNotItsTarget()
        {
            using (var dir = new TempDir())
            {
                string outside = Directory.CreateDirectory(dir.File("outside")).FullName;
                File.WriteAllText(Path.Combine(outside, "precious.txt"), "à garder");
                string work = Directory.CreateDirectory(dir.File("work")).FullName;
                Directory.CreateDirectory(Path.Combine(work, "CrapFixer"));
                Junction(Path.Combine(work, @"CrapFixer\link"), outside);

                WorkFolder.Delete(work);

                Assert.False(Directory.Exists(work));
                Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
            }
        }

        [Fact]
        public void Delete_RefusesWhenTheFolderItselfIsALink()
        {
            using (var dir = new TempDir())
            {
                string outside = Directory.CreateDirectory(dir.File("outside")).FullName;
                File.WriteAllText(Path.Combine(outside, "precious.txt"), "à garder");
                Junction(dir.File("work"), outside);

                Assert.Throws<IOException>(() => WorkFolder.Delete(dir.File("work")));
                Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
                Directory.Delete(dir.File("work"));
            }
        }
    }
}
