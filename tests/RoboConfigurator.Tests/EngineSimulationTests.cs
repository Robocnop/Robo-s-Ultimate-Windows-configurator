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

        [Fact]
        public void SimulatedFailures_EachOutcomeIsReportedAndOnlySuccessIsRecorded()
        {
            using (var dir = new TempDir())
            {
                Lang.Set("en");
                var config = new RunConfig { Steps = new[] { StepNames.Ninite, StepNames.CrapFixer, StepNames.Edge, StepNames.Office, StepNames.Activation } };
                config.Save(dir.File("configuration.json"));
                var items = Items();
                var state = new StepState(dir.File("etapes-terminees.txt"));
                var engine = new Engine(config, dir.Path, dir.File("configuration.json"), state, null, true, items)
                {
                    SimulatedFailures = Options.Parse(new[] { "-Simulation", "-SimulateFail", "Ninite:download,Edge,Office:restart,Activation:notdone" }).SimulateFail,
                };

                var result = engine.Run();

                Assert.Equal(new[] { StepNames.Ninite, StepNames.Edge }, result.Failed);
                Assert.Equal(new[] { StepNames.Office }, result.AfterRestart);
                Assert.Equal(new[] { StepNames.Activation }, result.NotDone);
                // Seule l'étape réussie est mémorisée : les autres seront refaites au prochain lancement
                Assert.True(state.IsDone(StepNames.CrapFixer));
                Assert.All(new[] { StepNames.Ninite, StepNames.Edge, StepNames.Office, StepNames.Activation }, s => Assert.False(state.IsDone(s)));
                // Configuration gardée pour la reprise, pas de nettoyage
                Assert.True(File.Exists(dir.File("configuration.json")));
                Assert.Equal(StepStatus.Skipped, items[Engine.CleanUpKey].Status);
                string report = File.ReadAllText(result.ReportPath);
                Assert.Contains("FAILED", report);
                Assert.Contains("NOT DONE", report);
                Assert.Contains("TO FINISH after restarting", report);
            }
        }

        [Fact]
        public void StopRequested_NoStepStarted_SettingsKept()
        {
            using (var dir = new TempDir())
            {
                var config = new RunConfig { NewName = "SIMULATION-PC" };
                config.Save(dir.File("configuration.json"));
                var items = Items();
                var state = new StepState(dir.File("etapes-terminees.txt"));
                var engine = new Engine(config, dir.Path, dir.File("configuration.json"), state, null, true, items);

                engine.RequestStop();
                var result = engine.Run();

                Assert.True(result.StopRequested);
                Assert.Equal(StepNames.All, result.Stopped);
                Assert.All(StepNames.All, s => Assert.False(state.IsDone(s)));
                Assert.Equal(StepStatus.Skipped, items[Engine.RenameKey].Status);
                Assert.Equal(StepStatus.Skipped, items[Engine.CleanUpKey].Status);
                Assert.True(File.Exists(dir.File("configuration.json")));
                Assert.True(File.Exists(result.ReportPath));
            }
        }

        [Fact]
        public void UnexpectedErrorOutsideSteps_IsReturnedNotThrown()
        {
            using (var dir = new TempDir())
            {
                // Un dossier à la place du fichier de configuration : sa suppression en fin de déroulement échoue
                string configFile = Directory.CreateDirectory(dir.File("configuration.json")).FullName;
                var config = new RunConfig { Steps = new string[0], CleanUp = false };
                var items = Items();

                var result = new Engine(config, dir.Path, configFile, new StepState(dir.File("s.txt")), null, true, items).Run();

                Assert.NotNull(result.UnexpectedError);
                Assert.NotNull(result.ReportPath);
            }
        }
    }
}
