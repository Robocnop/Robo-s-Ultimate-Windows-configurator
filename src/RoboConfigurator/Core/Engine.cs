using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace RoboConfigurator.Core
{
    public sealed class RunResult
    {
        public List<string> Failed { get; } = new List<string>();
        public string ReportPath { get; set; }
        public string PendingName { get; set; }
    }

    /// <summary>Enchaîne les téléchargements, les étapes, le renommage et le rapport, sans interaction.</summary>
    public sealed class Engine
    {
        public const string RenameKey = "Rename";
        public const string ReportKey = "Report";
        public const string CleanUpKey = "CleanUp";

        private readonly RunConfig _config;
        private readonly string _workDir;
        private readonly string _configFile;
        private readonly StepState _state;
        private readonly OemKey _oemKey;
        private readonly bool _simulation;
        private readonly IDictionary<string, StepItem> _items;
        private readonly RunResult _result = new RunResult();

        public Engine(RunConfig config, string workDir, string configFile, StepState state, OemKey oemKey,
                      bool simulation, IDictionary<string, StepItem> items)
        {
            _config = config; _workDir = workDir; _configFile = configFile; _state = state;
            _oemKey = oemKey; _simulation = simulation; _items = items;
        }

        private void Set(string key, StepStatus status, string detail = null)
        {
            _items[key].Status = status;
            if (detail != null) _items[key].Detail = detail;
        }

        private void Detail(string key, string detail) => _items[key].Detail = detail;

        public RunResult Run()
        {
            Log.Info($"Windows {(SystemInfo.IsWin11 ? "11" : "10")} - dossier de travail : {_workDir}");
            if (_simulation) Log.Warn("Mode simulation : aucune modification n'est faite sur ce PC.");

            var todo = new List<string>();
            foreach (string step in StepNames.All)
            {
                if (!_config.Has(step)) Set(step, StepStatus.Skipped, "Non cochée dans la configuration");
                else if (_state.IsDone(step)) Set(step, StepStatus.AlreadyDone, "Faite lors d'un lancement précédent");
                else { Set(step, StepStatus.Pending, "En attente"); todo.Add(step); }
            }

            Log.Title("Téléchargements");
            foreach (string step in todo)
            {
                var payload = Payloads.ForStep(step);
                if (payload == null) continue;
                Set(step, StepStatus.Downloading, $"Préparation de {payload.File}...");
                try
                {
                    if (_simulation) Simulate(step, $"{payload.File} prêt (simulation)");
                    else Payloads.Ensure(payload, _workDir, p => Detail(step, p >= 0 ? $"Téléchargement de {payload.File} : {p} %" : $"Téléchargement de {payload.File}..."));
                    Set(step, StepStatus.Pending, "Prête");
                }
                catch (Exception ex) { Fail(step, ex.Message); }
            }

            foreach (string step in todo.Where(s => !_result.Failed.Contains(s)))
            {
                Log.Title(step);
                Set(step, StepStatus.Running, "");
                try
                {
                    string warning = _simulation ? SimulateStep(step) : RunStep(step);
                    _state.MarkDone(step);
                    Set(step, warning == null ? StepStatus.Done : StepStatus.Warning, warning ?? "Terminée");
                    Log.Ok($"{step} terminé");
                }
                catch (Exception ex) { Fail(step, ex.Message); }
            }

            Log.Title("Résumé");
            if (_result.Failed.Any()) Log.Warn($"Étapes en échec : {string.Join(", ", _result.Failed)}. Relancer l'application pour les réessayer.");
            else Log.Ok("Tout est terminé.");

            RenamePc();
            WriteReport();

            // Tout est fait : la configuration n'est plus utile (elle peut contenir la clé Windows)
            if (!_result.Failed.Any()) File.Delete(_configFile);
            CleanUp();
            return _result;
        }

        private void Fail(string step, string message)
        {
            Log.Error(message);
            _result.Failed.Add(step);
            Set(step, StepStatus.Failed, message);
        }

        // -------------------------------------------------------------------
        // Étapes
        // -------------------------------------------------------------------
        private string RunStep(string step)
        {
            switch (step)
            {
                case StepNames.Ninite: return Ninite();
                case StepNames.CrapFixer: return CrapFixerStep();
                case StepNames.Edge: return Edge();
                case StepNames.Office: return Office();
                case StepNames.Activation: return Activation();
                default: throw new ArgumentException(step);
            }
        }

        private string Ninite()
        {
            Detail(StepNames.Ninite, "Cliquez sur « Close » dans Ninite quand il affiche « Done ».");
            Log.Info("Ninite installe les logiciels automatiquement.");
            SystemInfo.RunVisible(Path.Combine(_workDir, Payloads.Ninite.File));
            return null;
        }

        private string CrapFixerStep()
        {
            const string key = StepNames.CrapFixer;
            string dest = Path.Combine(_workDir, "CrapFixer");
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            ZipFile.ExtractToDirectory(Path.Combine(_workDir, Payloads.CrapFixer.File), dest);
            string tweakIni = Directory.GetFiles(dest, "Wintweak2.ini", SearchOption.AllDirectories).FirstOrDefault();
            string appIni = Directory.GetFiles(dest, "Winappx.ini", SearchOption.AllDirectories).FirstOrDefault();
            if (tweakIni == null || appIni == null) throw new Exception("Wintweak2.ini ou Winappx.ini introuvable dans l'archive CrapFixer");

            // Tweaks registre : ceux cochés par défaut dans CrapFixer (ou tous) + thème sombre
            var tweaks = CrapFixer.SelectTweaks(CrapFixer.ReadIni(tweakIni), _config.AllTweaks).ToList();
            int errors = 0, done = 0;
            bool restartExplorer = false;
            foreach (var t in tweaks)
            {
                Detail(key, $"Réglage {++done}/{tweaks.Count} : {t.Name}");
                try
                {
                    CrapFixer.ApplyTweak(t);
                    Log.Ok(t.Name);
                    if (t["Restart"] == "explorer") restartExplorer = true;
                }
                catch (Exception ex) { errors++; Log.Error($"{t.Name} : {ex.Message}"); }
            }

            // Applications préinstallées cochées par défaut dans CrapFixer
            Detail(key, "Recherche des applications inutiles...");
            var installed = CrapFixer.InstalledAppxNames();
            foreach (var app in CrapFixer.ReadIni(appIni).Where(a => a.DefaultSelected && a.Has("PackageName")))
            {
                if (!installed.Any(n => CrapFixer.MatchesPackage(app["PackageName"], n))) continue;
                Detail(key, $"Suppression : {app.Name}");
                try { CrapFixer.RemoveAppx(app["PackageName"]); Log.Ok($"Appli supprimée : {app.Name}"); }
                catch (Exception ex) { errors++; Log.Error($"Suppression de {app.Name} : {ex.Message}"); }
            }

            if (restartExplorer)
            {
                Detail(key, "Redémarrage de l'explorateur...");
                Log.Info("Redémarrage de l'explorateur...");
                foreach (var p in Process.GetProcessesByName("explorer")) { try { p.Kill(); } catch (Exception) { } }
                Thread.Sleep(3000);
                // Windows relance normalement l'explorateur tout seul
                if (!Process.GetProcessesByName("explorer").Any()) Process.Start("explorer.exe");
            }
            if (errors > 0) throw new Exception($"{errors} élément(s) CrapFixer en échec (voir le journal)");
            return null;
        }

        private string Edge()
        {
            // Même méthode que WinUtil (Chris Titus) : un faux MicrosoftEdge.exe dans l'ancien dossier
            // d'Edge débloque le désinstalleur officiel. WebView2 (utilisé par Office, Teams...) est conservé.
            string edgeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application");
            string setup = !Directory.Exists(edgeDir) ? null : Directory.GetDirectories(edgeDir)
                .Select(d => Path.Combine(d, @"Installer\setup.exe"))
                .Where(File.Exists)
                .OrderBy(f => Version.TryParse(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f))), out var v) ? v : new Version())
                .LastOrDefault();
            if (setup == null) { Log.Ok("Edge n'est pas installé"); return null; }

            foreach (var p in Process.GetProcessesByName("msedge")) { try { p.Kill(); } catch (Exception) { } }
            string fake = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"SystemApps\Microsoft.MicrosoftEdge_8wekyb3d8bbwe\MicrosoftEdge.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(fake));
            File.WriteAllBytes(fake, new byte[0]);

            Detail(StepNames.Edge, "Désinstallation de Microsoft Edge...");
            Log.Info("Désinstallation de Microsoft Edge...");
            SystemInfo.Run(setup, "--uninstall --system-level --force-uninstall --delete-profile");

            if (File.Exists(Path.Combine(edgeDir, "msedge.exe"))) throw new Exception("Edge est toujours présent après la désinstallation");
            Log.Ok("Edge désinstallé");
            return null;
        }

        private string Office()
        {
            var word = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft Office\root\Office16\WINWORD.EXE"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft Office\root\Office16\WINWORD.EXE"),
            };
            bool Installed() => word.Any(File.Exists);
            if (Installed()) { Log.Ok("Office est déjà installé"); return null; }

            Detail(StepNames.Office, "Installation d'Office (cela peut prendre un moment)...");
            Log.Info("Installation d'Office (cela peut prendre un moment)...");
            var start = DateTime.Now;
            SystemInfo.RunVisible(Path.Combine(_workDir, Payloads.Office.File));
            // Le programme d'installation peut rendre la main avant la fin réelle
            while (!Installed() || Process.GetProcessesByName("OfficeSetup").Any())
            {
                var elapsed = DateTime.Now - start;
                if (elapsed.TotalMinutes > 60) throw new Exception("Office ne semble pas installé après 60 minutes");
                Detail(StepNames.Office, $"Installation d'Office en cours ({(int)elapsed.TotalMinutes} min)...");
                Thread.Sleep(15000);
            }
            Log.Ok("Office installé");
            return null;
        }

        private string Activation()
        {
            const string key = StepNames.Activation;
            string slmgr = Path.Combine(Environment.SystemDirectory, "slmgr.vbs");
            void Slmgr(string args) => SystemInfo.Run("cscript.exe", $"//nologo \"{slmgr}\" {args}");

            // 1) Si une clé Windows a été fournie, on l'installe en priorité
            if (!SystemInfo.IsWindowsActivated() && _config.WindowsKey.Length > 0)
            {
                Detail(key, "Installation de la clé Windows fournie...");
                Log.Info("Installation de la clé Windows fournie...");
                Slmgr("/ipk " + _config.WindowsKey);
                Slmgr("/ato");
            }

            // Sinon, clé enregistrée dans le BIOS par le fabricant (PC de marque)
            if (!SystemInfo.IsWindowsActivated() && _config.WindowsKey.Length == 0 && _oemKey != null)
            {
                Detail(key, "Installation de la clé Windows du BIOS...");
                Log.Info($"Installation de la clé Windows du BIOS ({_oemKey.Description})...");
                Slmgr("/ipk " + _oemKey.Key);
                Slmgr("/ato");
            }

            if (SystemInfo.IsWindowsActivated()) { Log.Ok("Windows est activé"); return null; }

            // 2) Pas de clé ou échec : télécharger et exécuter le script d'activation
            Log.Warn("Windows n'est pas activé : téléchargement du script d'activation...");
            try
            {
                string script = Payloads.Ensure(Payloads.Mas, _workDir);
                Detail(key, "Activation de Windows (HWID)...");
                Log.Info("Lancement de l'activation Windows (option 1 - HWID)...");
                SystemInfo.RunVisible("cmd.exe", $"/c \"{script}\" /HWID");
                Detail(key, "Activation d'Office (Ohook)...");
                Log.Info("Lancement de l'activation Office (option 2 - Ohook)...");
                SystemInfo.RunVisible("cmd.exe", $"/c \"{script}\" /Ohook");
            }
            catch (Exception ex)
            {
                Log.Error($"Impossible de télécharger ou lancer le script d'activation : {ex.Message}");
                return "Script d'activation indisponible : activer Windows manuellement.";
            }

            if (SystemInfo.IsWindowsActivated()) { Log.Ok("Windows est activé avec succès"); return null; }
            Log.Warn("Le script s'est exécuté mais Windows ne semble pas activé. Vérifier manuellement.");
            return "Windows ne semble pas activé : vérifier manuellement.";
        }

        // -------------------------------------------------------------------
        // Fin : renommage, rapport et nettoyage
        // -------------------------------------------------------------------
        private void RenamePc()
        {
            string name = _config.NewName;
            if (string.IsNullOrEmpty(name) || name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                Set(RenameKey, StepStatus.Skipped, $"Le PC garde son nom ({Environment.MachineName})");
                return;
            }
            Log.Title("Nom du PC");
            Set(RenameKey, StepStatus.Running, $"{Environment.MachineName} -> {name}");
            try
            {
                if (!_simulation) SystemInfo.RenameComputer(name);
                _result.PendingName = name;
                Log.Ok($"Le PC s'appellera « {name} » après le redémarrage");
                Set(RenameKey, StepStatus.Done, $"Le PC s'appellera « {name} » après le redémarrage");
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
                Set(RenameKey, StepStatus.Failed, ex.Message);
            }
        }

        private void WriteReport()
        {
            Log.Title("Rapport du poste");
            Set(ReportKey, StepStatus.Running, "Collecte des informations du poste...");
            try
            {
                var status = StepNames.All.Select(s =>
                {
                    string state = !_config.Has(s) ? "ignorée"
                                 : _result.Failed.Contains(s) ? "EN ÉCHEC"
                                 : _state.IsDone(s) ? "OK"
                                 : "non faite";
                    return $"{s,-15} : {state}";
                });
                var lines = Report.Build(_result.PendingName, _oemKey, status);
                string file = $"Rapport-{_result.PendingName ?? Environment.MachineName}-{DateTime.Now:yyyy-MM-dd}.txt";
                string path = Path.Combine(_workDir, file);
                File.WriteAllLines(path, lines, new UTF8Encoding(true));
                string desktopCopy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), file);
                if (!_simulation) File.Copy(path, desktopCopy, true);
                // La copie du Bureau survit au nettoyage du dossier de travail
                _result.ReportPath = _simulation ? path : desktopCopy;
                Log.Ok($"Rapport enregistré{(_simulation ? "" : " sur le Bureau et")} dans {path}");
                Set(ReportKey, StepStatus.Done, _simulation ? path : $"Copié sur le Bureau : {file}");
            }
            catch (Exception ex)
            {
                Log.Error($"Impossible de générer le rapport : {ex.Message}");
                Set(ReportKey, StepStatus.Failed, ex.Message);
            }
        }

        private void CleanUp()
        {
            if (!_config.CleanUp)
            {
                Set(CleanUpKey, StepStatus.Skipped, $"Fichiers conservés dans {_workDir}");
                return;
            }
            if (_result.Failed.Any())
            {
                Set(CleanUpKey, StepStatus.Skipped, "Fichiers conservés pour pouvoir reprendre les étapes en échec");
                return;
            }
            Log.Title("Nettoyage");
            Set(CleanUpKey, StepStatus.Running, $"Suppression de {_workDir}...");
            if (_simulation)
            {
                Log.Ok($"Le dossier {_workDir} serait supprimé (simulation)");
                Set(CleanUpKey, StepStatus.Done, "Dossier de travail supprimé (simulation)");
                return;
            }
            Log.Info($"Suppression du dossier de travail {_workDir} (téléchargements, configuration, journal)");
            Log.Close();
            try
            {
                int pending = DeleteFolder(_workDir);
                string detail = pending == 0 ? $"Dossier {_workDir} supprimé"
                              : $"Dossier {_workDir} supprimé ({pending} fichier(s) en cours d'utilisation supprimé(s) au redémarrage)";
                Log.Ok(detail);
                Set(CleanUpKey, StepStatus.Done, detail);
            }
            catch (Exception ex)
            {
                Log.Error($"Nettoyage incomplet : {ex.Message}");
                Set(CleanUpKey, StepStatus.Warning, ex.Message);
            }
        }

        /// <summary>Supprime le dossier ; ce qui est verrouillé (exe lancé depuis ce dossier...) part au redémarrage.</summary>
        private static int DeleteFolder(string dir)
        {
            int pending = 0;
            foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); }
                catch (Exception) { SystemInfo.DeleteAtReboot(file); pending++; }
            }
            // Dossiers du plus profond au dossier de travail lui-même, après leurs fichiers
            var dirs = Directory.GetDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).Concat(new[] { dir });
            foreach (string d in dirs)
            {
                try { Directory.Delete(d); }
                catch (Exception) { SystemInfo.DeleteAtReboot(d); }
            }
            return pending;
        }

        // -------------------------------------------------------------------
        // Simulation : parcourt l'interface sans rien modifier
        // -------------------------------------------------------------------
        private void Simulate(string key, string message)
        {
            for (int p = 0; p <= 100; p += 20) { Detail(key, $"{message.Split(' ')[0]} : {p} %"); Thread.Sleep(150); }
            Log.Ok(message);
        }

        private string SimulateStep(string step)
        {
            string[] actions;
            switch (step)
            {
                case StepNames.Ninite: actions = new[] { "Cliquez sur « Close » dans Ninite quand il affiche « Done »." }; break;
                case StepNames.CrapFixer: actions = Enumerable.Range(1, 6).Select(i => $"Réglage {i * 8}/47").ToArray(); break;
                case StepNames.Office: actions = new[] { "Installation d'Office (cela peut prendre un moment)..." }; break;
                default: actions = new[] { $"{step} : simulation..." }; break;
            }
            foreach (string a in actions) { Detail(step, a); Log.Info(a + " (simulation)"); Thread.Sleep(900); }
            return step == StepNames.Activation ? "Exemple d'avertissement : vérifier l'activation manuellement." : null;
        }
    }
}
