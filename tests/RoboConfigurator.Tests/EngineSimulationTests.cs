using System.IO;
using System.Linq;
using RoboConfigurator.Core;
using Wpf.Ui.Controls;
using Xunit;

namespace RoboConfigurator.Tests
{
    /// <summary>Déroulement complet en mode simulation, sans fenêtre : rien n'est modifié sur le PC.</summary>
    public class EngineSimulationTests
    {
        internal static System.Collections.Generic.Dictionary<string, StepItem> Items() =>
            StepNames.All.Concat(new[] { Engine.RenameKey, Engine.ReportKey, Engine.CleanUpKey })
                .ToDictionary(k => k, k => new StepItem(k, k, "", SymbolRegular.Circle24));

        [Fact]
        public void Simulation_RunsEveryStep()
        {
            using (var dir = new TempDir())
            {
                var config = new RunConfig { NewName = "SIMULATION-PC", AutoRestart = false };
                var items = Items();
                var state = new StepState(dir.File("etapes-terminees.txt"));
                config.Save(dir.File("configuration.json"));

                var result = new Engine(config, dir.Path, dir.File("configuration.json"), state, null, true, items).Run();

                Assert.Empty(result.Failed);
                foreach (string step in StepNames.All)
                    Assert.Contains(items[step].Status, new[] { StepStatus.Done, StepStatus.Warning });
                Assert.Equal(StepStatus.Done, items[Engine.RenameKey].Status);
                Assert.Equal(StepStatus.Done, items[Engine.ReportKey].Status);
                Assert.True(File.Exists(result.ReportPath));
                Assert.StartsWith(dir.Path, result.ReportPath);
                // Le rapport donne l'état réel de chaque étape avec son détail
                string report = File.ReadAllText(result.ReportPath);
                Assert.Contains("Activation", report);
                Assert.Contains(items[StepNames.Activation].Detail, report);
                Assert.Contains(Engine.RenameKey, report);
                // Simulation : le dossier de travail n'est pas supprimé
                Assert.True(Directory.Exists(dir.Path));
            }
        }
    }
}
