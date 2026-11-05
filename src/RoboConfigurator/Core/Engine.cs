using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace RoboConfigurator.Core
{
    public sealed class RunResult
    {
        public List<string> Failed { get; } = new List<string>();
        /// <summary>Étapes à terminer après un redémarrage (changement d'édition de Windows...).</summary>
        public List<string> AfterRestart { get; } = new List<string>();
        /// <summary>Étapes déroulées sans atteindre leur but (Windows non activé...) : réessayées au prochain lancement.</summary>
        public List<string> NotDone { get; } = new List<string>();
        /// <summary>Étapes non lancées parce que l'arrêt a été demandé (bouton « Arrêter après cette étape »).</summary>
        public List<string> Stopped { get; } = new List<string>();
        public bool StopRequested { get; set; }
        /// <summary>Étapes en échec, non faites ou non lancées : la configuration et les fichiers sont gardés pour les reprendre.</summary>
        public bool HasUnfinished => Failed.Any() || NotDone.Any() || Stopped.Any();
        /// <summary>Erreur inattendue qui a arrêté la configuration (hors des étapes), ou null.</summary>
        public string UnexpectedError { get; set; }
        public string ReportPath { get; set; }
        public string PendingName { get; set; }
        /// <summary>Copie du journal sur le Bureau quand le dossier de travail est nettoyé malgré une erreur.</summary>
        public string LogCopyPath { get; set; }
    }

    /// <summary>L'étape a fait sa part mais ne pourra se terminer qu'après un redémarrage (puis un nouveau lancement).</summary>
    public sealed class RestartRequiredException : Exception
    {
        public RestartRequiredException(string message) : base(message) { }
    }

    /// <summary>
    /// L'étape s'est déroulée mais n'a pas atteint son but (Windows non activé...) : affichée « À vérifier »,
    /// elle n'est pas marquée faite et sera réessayée au prochain lancement.
    /// </summary>
    public sealed class StepNotDoneException : Exception
    {
        public StepNotDoneException(string message) : base(message) { }
    }

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

        /// <summary>Étape en cours, pour savoir laquelle marquer en échec si une erreur inattendue arrête tout.</summary>
        private string _current;

        private volatile bool _stop;

        /// <summary>
        /// Demandé depuis l'interface : l'étape en cours se termine normalement, les suivantes ne sont pas lancées.
        /// Pas d'arrêt au milieu d'une étape (Edge, barre des tâches...) : elle laisserait le PC dans un état intermédiaire.
        /// </summary>
        public void RequestStop()
        {
            if (_stop) return;
            _stop = true;
            Log.Warn(Lang.T("Stop requested: the current step finishes, the next ones will not be started",
                            "Arrêt demandé : l'étape en cours se termine, les suivantes ne seront pas lancées"));
        }

        /// <summary>Si l'arrêt est demandé, marque l'étape comme non lancée et renvoie true.</summary>
        private bool StopBefore(string step)
        {
            if (!_stop) return false;
            if (!_result.Stopped.Contains(step)) _result.Stopped.Add(step);
            Set(step, StepStatus.Skipped, Lang.T("Not started: stop requested", "Non lancée : arrêt demandé"));
            return true;
        }

        /// <summary>Ne lève jamais d'exception : une erreur inattendue est journalisée et rendue dans <see cref="RunResult.UnexpectedError"/>.</summary>
        public RunResult Run()
        {
            try { RunAll(); }
            catch (Exception ex)
            {
                Log.Error(Lang.T("Unexpected error: the setup stopped", "Erreur inattendue : la configuration s'est arrêtée"), ex);
                _result.UnexpectedError = ex.GetBaseException().Message;
                if (_current != null && !_result.Failed.Contains(_current))
                {
                    _result.Failed.Add(_current);
                    Set(_current, StepStatus.Failed, _result.UnexpectedError);
                }
                // Le rapport reste utile pour comprendre ; la configuration et les fichiers sont gardés pour reprendre
                if (_result.ReportPath == null) try { WriteReport(); } catch (Exception) { }
            }
            _result.StopRequested = _stop;
            return _result;
        }

        private void RunAll()
        {
            Log.Info(Lang.T($"Windows {(SystemInfo.IsWin11 ? "11" : "10")} - working folder: {_workDir}", $"Windows {(SystemInfo.IsWin11 ? "11" : "10")} - dossier de travail : {_workDir}"));
            LogDiagnostics();
            if (_simulation) Log.Warn(Lang.T("Simulation mode: nothing is changed on this PC.", "Mode simulation : aucune modification n'est faite sur ce PC."));

            var todo = new List<string>();
            foreach (string step in StepNames.All)
            {
                if (!_config.Has(step)) Set(step, StepStatus.Skipped, Lang.T("Not selected in the settings", "Non cochée dans la configuration"));
                else if (_state.IsDone(step)) Set(step, StepStatus.AlreadyDone, Lang.T("Done during a previous run", "Faite lors d'un lancement précédent"));
                else { Set(step, StepStatus.Pending, Lang.T("Waiting", "En attente")); todo.Add(step); }
            }

            Log.Title(Lang.T("Downloads", "Téléchargements"));
            foreach (string step in todo)
            {
                var payload = Payloads.ForStep(step, _config);
                if (payload == null || StopBefore(step)) continue;
                _current = step;
                Set(step, StepStatus.Downloading, Lang.T($"Preparing {payload.File}...", $"Préparation de {payload.File}..."));
                try
                {
                    if (_simulation) Simulate(step, Lang.T($"{payload.File} ready (simulation)", $"{payload.File} prêt (simulation)"));
                    else Payloads.Ensure(payload, _workDir, p => Detail(step, p >= 0 ? Lang.T($"Downloading {payload.File}: {p}%", $"Téléchargement de {payload.File} : {p} %")
                                                                                      : Lang.T($"Downloading {payload.File}...", $"Téléchargement de {payload.File}...")));
                    Set(step, StepStatus.Pending, Lang.T("Ready", "Prête"));
                }
                catch (Exception ex) { Fail(step, ex); }
            }

            foreach (string step in todo.Where(s => !_result.Failed.Contains(s)))
            {
                if (StopBefore(step)) continue;
                _current = step;
                Log.Title(StepNames.Display(step));
                Set(step, StepStatus.Running, "");
                var start = DateTime.Now;
                try
                {
                    string warning = _simulation ? SimulateStep(step) : RunStep(step);
                    _state.MarkDone(step);
                    if (step == StepNames.Activation) ForgetWindowsKey();
                    Set(step, warning == null ? StepStatus.Done : StepStatus.Warning, warning ?? Lang.T("Done", "Terminée"));
                    if (warning != null) Log.Warn(Lang.T($"{StepNames.Display(step)} needs checking: {warning}", $"{StepNames.Display(step)} à vérifier : {warning}"));
                    Log.Ok(Lang.T($"{StepNames.Display(step)} done in {(DateTime.Now - start).TotalSeconds:0} s", $"{StepNames.Display(step)} terminé en {(DateTime.Now - start).TotalSeconds:0} s"));
                }
                catch (RestartRequiredException ex)
                {
                    // Pas marquée comme faite : elle sera reprise au prochain lancement, après le redémarrage
                    _result.AfterRestart.Add(step);
                    Set(step, StepStatus.Warning, ex.Message);
                    Log.Warn(Lang.T($"{StepNames.Display(step)} will finish after restarting: {ex.Message}", $"{StepNames.Display(step)} se terminera après le redémarrage : {ex.Message}"));
                }
                catch (StepNotDoneException ex)
                {
                    // Pas marquée comme faite : elle sera réessayée au prochain lancement
                    _result.NotDone.Add(step);
                    Set(step, StepStatus.Warning, ex.Message);
                    Log.Warn(Lang.T($"{StepNames.Display(step)} not done after {(DateTime.Now - start).TotalSeconds:0} s, it will be retried at the next launch: {ex.Message}",
                                    $"{StepNames.Display(step)} non faite après {(DateTime.Now - start).TotalSeconds:0} s, elle sera réessayée au prochain lancement : {ex.Message}"));
                }
                catch (Exception ex) { Fail(step, ex, DateTime.Now - start); }
            }

            _current = null;
            Log.Title(Lang.T("Summary", "Résumé"));
            if (_result.Failed.Any()) Log.Warn(Lang.T($"Failed steps: {StepNames.Display(_result.Failed)}. Run the app again to retry them.", $"Étapes en échec : {StepNames.Display(_result.Failed)}. Relancer l'application pour les réessayer."));
            if (_result.NotDone.Any()) Log.Warn(Lang.T($"Steps not done: {StepNames.Display(_result.NotDone)}. Run the app again to retry them.", $"Étapes non faites : {StepNames.Display(_result.NotDone)}. Relancer l'application pour les réessayer."));
            if (_result.AfterRestart.Any()) Log.Warn(Lang.T($"To finish after restarting: {StepNames.Display(_result.AfterRestart)}. Run the app again after the restart.", $"À terminer après le redémarrage : {StepNames.Display(_result.AfterRestart)}. Relancer l'application après le redémarrage."));
            // Les étapes avec un téléchargement sont arrêtées en premier : remises dans l'ordre d'affichage
            _result.Stopped.Sort((a, b) => Array.IndexOf(StepNames.All, a).CompareTo(Array.IndexOf(StepNames.All, b)));
            if (_result.Stopped.Any()) Log.Warn(Lang.T($"Stopped at your request. Not started: {StepNames.Display(_result.Stopped)}. Run the app again to continue.", $"Arrêt demandé. Non lancées : {StepNames.Display(_result.Stopped)}. Relancer l'application pour continuer."));
            if (!_result.HasUnfinished && !_result.AfterRestart.Any()) Log.Ok(Lang.T("All done.", "Tout est terminé."));

            RenamePc();
            WriteReport();

            _current = null;
            // Tout est fait : la configuration n'est plus utile (elle peut contenir la clé Windows)
            if (!_result.HasUnfinished && !_result.AfterRestart.Any()) File.Delete(_configFile);
            CleanUp();
            _current = null;
        }

        /// <summary>L'activation est faite : la clé Windows n'a plus rien à faire dans le fichier de reprise.</summary>
        private void ForgetWindowsKey()
        {
            if (_config.WindowsKey.Length == 0 || !File.Exists(_configFile)) return;
            try
            {
                _config.WindowsKey = "";
                _config.Save(_configFile);
                Log.Info(Lang.T("Windows key removed from the saved settings", "Clé Windows retirée de la configuration enregistrée"));
            }
            catch (Exception ex) { Log.Warn(Lang.T($"Could not remove the Windows key from the saved settings: {ex.Message}", $"Impossible de retirer la clé Windows de la configuration enregistrée : {ex.Message}")); }
        }

        private void LogDiagnostics()
        {
            string Try(Func<string> read) { try { return read(); } catch (Exception ex) { return "? (" + ex.Message + ")"; } }
            string B(bool value) => value ? Lang.T("yes", "oui") : Lang.T("no", "non");
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            Log.Info(Lang.T($"App: version {version}", $"Application : version {version}"));
            Log.Info($"Windows{Lang.T(": ", " : ")}{Try(SystemInfo.WindowsCaption)} (build {SystemInfo.BuildNumber}, EditionID {Try(SystemInfo.EditionId)}, {(Environment.Is64BitOperatingSystem ? "64" : "32")} bits)");
            Log.Info(Lang.T($"User: {Environment.UserDomainName}\\{Environment.UserName}, administrator: {(SystemInfo.IsAdministrator() ? "yes" : "no")}",
                            $"Utilisateur : {Environment.UserDomainName}\\{Environment.UserName}, administrateur : {(SystemInfo.IsAdministrator() ? "oui" : "non")}"));
            Log.Info(Lang.T($"Language: {System.Globalization.CultureInfo.CurrentUICulture.Name}, region: {System.Globalization.RegionInfo.CurrentRegion.Name}",
                            $"Langue : {System.Globalization.CultureInfo.CurrentUICulture.Name}, région : {System.Globalization.RegionInfo.CurrentRegion.Name}"));
            Log.Info(Try(() =>
            {
                var drive = new DriveInfo(Path.GetPathRoot(_workDir));
                return Lang.T($"Free space on {drive.Name}: {drive.AvailableFreeSpace / (1L << 30)} GB", $"Espace libre sur {drive.Name} : {drive.AvailableFreeSpace / (1L << 30)} Go");
            }));
            Log.Info(Lang.T($"Settings: steps {string.Join(", ", _config.Steps)}; all tweaks {B(_config.AllTweaks)}; edition {(_config.Edition == "" ? "keep" : _config.Edition)}; Windows key {(_config.WindowsKey.Length > 0 ? "provided" : "none")}; restart {B(_config.AutoRestart)}; clean-up {B(_config.CleanUp)} (even on error: {B(_config.CleanUpOnError)}); Ninite apps {string.Join(",", _config.NiniteApps)}",
                            $"Configuration : étapes {string.Join(", ", _config.Steps)} ; tous les tweaks {B(_config.AllTweaks)} ; édition {(_config.Edition == "" ? "garder" : _config.Edition)} ; clé Windows {(_config.WindowsKey.Length > 0 ? "fournie" : "aucune")} ; redémarrage {B(_config.AutoRestart)} ; nettoyage {B(_config.CleanUp)} (même en cas d'erreur : {B(_config.CleanUpOnError)}) ; applis Ninite {string.Join(",", _config.NiniteApps)}"));
        }

        private void Fail(string step, Exception ex, TimeSpan? elapsed = null)
        {
            string after = elapsed == null ? "" : Lang.T($" after {elapsed.Value.TotalSeconds:0} s", $" après {elapsed.Value.TotalSeconds:0} s");
            Log.Error(Lang.T($"{StepNames.Display(step)} failed{after}", $"{StepNames.Display(step)} en échec{after}"), ex);
            _result.Failed.Add(step);
            Set(step, StepStatus.Failed, ex.GetBaseException().Message);
        }

        private string RunStep(string step)
        {
            switch (step)
            {
                case StepNames.Ninite: return Ninite();
                case StepNames.CrapFixer: return CrapFixerStep();
                case StepNames.Edge: return Edge();
                case StepNames.Taskbar: return Taskbar();
                case StepNames.Office: return Office();
                case StepNames.Activation: return Activation();
                default: throw new ArgumentException(step);
            }
        }

        private string Ninite()
        {
            Detail(StepNames.Ninite, Lang.T("Click “Close” in Ninite when it shows “Done”.", "Cliquez sur « Close » dans Ninite quand il affiche « Done »."));
            Log.Info(Lang.T($"Ninite installs: {NiniteApps.Names(_config.NiniteApps)}", $"Ninite installe : {NiniteApps.Names(_config.NiniteApps)}"));
            int code = SystemInfo.RunVisible(Path.Combine(_workDir, Payloads.ForStep(StepNames.Ninite, _config).File));
            // Fenêtre fermée avant la fin, erreur de téléchargement de Ninite... : l'étape sera refaite au prochain lancement
            if (code != 0)
                throw new StepNotDoneException(Lang.T($"Ninite closed with code {code} (0x{code:X8}): check that the apps are installed",
                                                      $"Ninite s'est fermé avec le code {code} (0x{code:X8}) : vérifier que les applis sont installées"));
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
            if (tweakIni == null || appIni == null) throw new Exception(Lang.T("Wintweak2.ini or Winappx.ini not found in the CrapFixer archive", "Wintweak2.ini ou Winappx.ini introuvable dans l'archive CrapFixer"));

            var tweaks = CrapFixer.SelectTweaks(CrapFixer.ReadIni(tweakIni), _config.AllTweaks).ToList();
            int errors = 0, done = 0;
            bool restartExplorer = false;
            foreach (var t in tweaks)
            {
                done++;
                Detail(key, Lang.T($"Tweak {done}/{tweaks.Count}: {t.Name}", $"Réglage {done}/{tweaks.Count} : {t.Name}"));
                try
                {
                    CrapFixer.ApplyTweak(t);
                    Log.Ok(t.Name);
                    if (t["Restart"] == "explorer") restartExplorer = true;
                }
                catch (Exception ex) { errors++; Log.Error($"{t.Name} ({t["Path"]} : {t["ValueName"]} = {t["RecommendedValue"]})", ex); }
            }

            Detail(key, Lang.T("Looking for bloatware...", "Recherche des applications inutiles..."));
            var installed = CrapFixer.InstalledAppxNames();
            foreach (var app in CrapFixer.ReadIni(appIni).Where(a => a.DefaultSelected && a.Has("PackageName")))
            {
                if (!installed.Any(n => CrapFixer.MatchesPackage(app["PackageName"], n))) continue;
                Detail(key, Lang.T($"Removing: {app.Name}", $"Suppression : {app.Name}"));
                try { CrapFixer.RemoveAppx(app["PackageName"]); Log.Ok(Lang.T($"App removed: {app.Name}", $"Appli supprimée : {app.Name}")); }
                catch (Exception ex) { errors++; Log.Error(Lang.T($"Removing {app.Name} ({app["PackageName"]})", $"Suppression de {app.Name} ({app["PackageName"]})"), ex); }
            }

            if (restartExplorer)
            {
                string restarting = Lang.T("Restarting File Explorer...", "Redémarrage de l'explorateur...");
                Detail(key, restarting);
                Log.Info(restarting);
                foreach (var p in Process.GetProcessesByName("explorer")) { try { p.Kill(); } catch (Exception) { } }
                Thread.Sleep(3000);
                // Windows relance normalement l'explorateur tout seul
                if (!Process.GetProcessesByName("explorer").Any()) Process.Start("explorer.exe");
            }
            if (errors > 0) throw new Exception(Lang.T($"{errors} CrapFixer item(s) failed (see the log)", $"{errors} élément(s) CrapFixer en échec (voir le journal)"));
            return null;
        }

        private string Edge()
        {
            // Méthode WinUtil : un faux MicrosoftEdge.exe dans SystemApps débloque le désinstalleur officiel. WebView2 est conservé.
            string edgeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application");
            Log.Info(Lang.T($"Edge folder: {edgeDir} ({(Directory.Exists(edgeDir) ? "present" : "missing")})", $"Dossier d'Edge : {edgeDir} ({(Directory.Exists(edgeDir) ? "présent" : "absent")})"));
            var setups = !Directory.Exists(edgeDir) ? new List<string>() : Directory.GetDirectories(edgeDir)
                .Select(d => Path.Combine(d, @"Installer\setup.exe"))
                .Where(File.Exists)
                .OrderBy(f => Version.TryParse(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f))), out var v) ? v : new Version())
                .ToList();
            foreach (string s in setups) Log.Info(Lang.T($"Uninstaller found: {s}", $"Désinstalleur trouvé : {s}"));
            string setup = setups.LastOrDefault();
            if (setup == null)
            {
                if (File.Exists(Path.Combine(edgeDir, "msedge.exe")))
                    throw new Exception(Lang.T($"msedge.exe is present but no Installer\\setup.exe was found in {edgeDir}", $"msedge.exe est présent mais aucun Installer\\setup.exe n'a été trouvé dans {edgeDir}"));
                Log.Ok(Lang.T("Edge is not installed", "Edge n'est pas installé"));
                return null;
            }
            LogEdgeContext();

            var running = Process.GetProcessesByName("msedge");
            if (running.Length > 0) Log.Info(Lang.T($"Closing {running.Length} msedge process(es)...", $"Fermeture de {running.Length} processus msedge..."));
            foreach (var p in running)
            {
                try { p.Kill(); p.WaitForExit(5000); }
                catch (Exception ex) { Log.Warn(Lang.T($"Could not close msedge (PID {p.Id}): {ex.Message}", $"Impossible de fermer msedge (PID {p.Id}) : {ex.Message}")); }
            }
            int left = Process.GetProcessesByName("msedge").Length;
            if (left > 0) Log.Warn(Lang.T($"{left} msedge process(es) still running before uninstalling", $"{left} processus msedge toujours actif(s) avant la désinstallation"));

            RemoveEdgeLeftovers();
            string fake = FakeEdgeExe;
            Directory.CreateDirectory(Path.GetDirectoryName(fake));
            File.WriteAllBytes(fake, new byte[0]);
            Log.Info(Lang.T($"Dummy MicrosoftEdge.exe created: {fake}", $"Faux MicrosoftEdge.exe créé : {fake}"));
            string fakeDllhost = null;
            int code;
            var start = DateTime.Now;
            // Les deux faux fichiers sont supprimés quoi qu'il arrive (et au lancement suivant si l'application est coupée)
            try
            {
                // EdgeUpdate est 32 bits : la valeur est écrite dans les deux vues du registre
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                        using (var key = hklm.CreateSubKey(@"SOFTWARE\Microsoft\EdgeUpdateDev"))
                            key.SetValue("AllowUninstall", 1, RegistryValueKind.DWord);
                        Log.Info(Lang.T($"EdgeUpdateDev\\AllowUninstall set to 1 ({view})", $"EdgeUpdateDev\\AllowUninstall mis à 1 ({view})"));
                    }
                    catch (Exception ex) { Log.Warn(Lang.T($"Could not set EdgeUpdateDev\\AllowUninstall ({view}): {ex.Message}", $"Impossible de créer EdgeUpdateDev\\AllowUninstall ({view}) : {ex.Message}")); }
                }

                // Les étiquettes d'expérimentation d'EdgeUpdate peuvent désactiver la désinstallation
                try
                {
                    using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                    using (var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\ClientState\{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}", true))
                    {
                        if (key?.GetValue("experiment_control_labels") != null)
                        {
                            key.DeleteValue("experiment_control_labels");
                            Log.Info(Lang.T("EdgeUpdate experiment_control_labels removed", "experiment_control_labels d'EdgeUpdate supprimé"));
                        }
                    }
                }
                catch (Exception ex) { Log.Warn(Lang.T($"Could not remove experiment_control_labels: {ex.Message}", $"Impossible de supprimer experiment_control_labels : {ex.Message}")); }

                // Depuis Windows 11 24H2, setup.exe refuse la désinstallation (code 532) si son processus parent
                // n'est pas dans une liste autorisée. Même méthode qu'ave9858 : une copie de cmd.exe nommée
                // dllhost.exe lance le désinstalleur.
                fakeDllhost = FakeDllhostPaths.FirstOrDefault(f => Directory.Exists(Path.GetDirectoryName(f)));
                File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), fakeDllhost, true);
                Log.Info(Lang.T($"Uninstaller launcher: {fakeDllhost} (copy of cmd.exe)", $"Lanceur du désinstalleur : {fakeDllhost} (copie de cmd.exe)"));

                Detail(StepNames.Edge, Lang.T("Uninstalling Microsoft Edge...", "Désinstallation de Microsoft Edge..."));
                const string args = "--uninstall --msedge --channel=stable --system-level --verbose-logging --force-uninstall --delete-profile";
                Log.Info(Lang.T($"Command: \"{setup}\" {args}", $"Commande : \"{setup}\" {args}"));
                start = DateTime.Now;
                code = SystemInfo.Run(fakeDllhost, $"/c \"\"{setup}\" {args}\"", display: $"setup.exe {args}", timeout: TimeSpan.FromMinutes(10));
            }
            finally
            {
                if (fakeDllhost != null) try { File.Delete(fakeDllhost); } catch (Exception) { }
                try { File.Delete(fake); } catch (Exception) { }
            }
            Log.Info(Lang.T($"The uninstaller returned after {(DateTime.Now - start).TotalSeconds:0} s with code {code}{EdgeExitCode(code)}", $"Le désinstalleur a rendu la main en {(DateTime.Now - start).TotalSeconds:0} s avec le code {code}{EdgeExitCode(code)}"));
            CopyEdgeInstallerLog(start);

            string exe = Path.Combine(edgeDir, "msedge.exe");
            if (File.Exists(exe))
            {
                Log.Error(Lang.T($"{exe} is still present (version {FileVersionInfo.GetVersionInfo(exe).FileVersion})", $"{exe} est toujours présent (version {FileVersionInfo.GetVersionInfo(exe).FileVersion})"));
                left = Process.GetProcessesByName("msedge").Length;
                if (left > 0) Log.Error(Lang.T($"{left} msedge process(es) running after uninstalling (Edge may have been restarted)", $"{left} processus msedge actif(s) après la désinstallation (Edge a pu être relancé)"));
                LogEdgeUninstallEntry();
                throw new Exception(Lang.T($"Edge is still present after uninstalling (code {code}{EdgeExitCode(code)}, see the log)", $"Edge est toujours présent après la désinstallation (code {code}{EdgeExitCode(code)}, voir le journal)"));
            }
            Log.Ok(Lang.T("Edge uninstalled", "Edge désinstallé"));
            return null;
        }

        private static string FakeEdgeExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"SystemApps\Microsoft.MicrosoftEdge_8wekyb3d8bbwe\MicrosoftEdge.exe");

        /// <summary>Emplacements possibles de la copie de cmd.exe nommée dllhost.exe, dans l'ordre de préférence.</summary>
        private static string[] FakeDllhostPaths => new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"SystemTemp\dllhost.exe"),
            Path.Combine(Path.GetTempPath(), "dllhost.exe"),
        };

        /// <summary>
        /// Supprime les faux fichiers d'une désinstallation d'Edge interrompue (application fermée, coupure de courant) :
        /// un MicrosoftEdge.exe vide dans SystemApps et une copie exacte de cmd.exe nommée dllhost.exe. Rien d'autre n'est touché.
        /// </summary>
        public static void RemoveEdgeLeftovers()
        {
            try
            {
                if (File.Exists(FakeEdgeExe) && new FileInfo(FakeEdgeExe).Length == 0)
                {
                    File.Delete(FakeEdgeExe);
                    Log.Info(Lang.T($"Leftover of an interrupted run removed: {FakeEdgeExe}", $"Reste d'un lancement interrompu supprimé : {FakeEdgeExe}"));
                }
                byte[] cmd = File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
                foreach (string copy in FakeDllhostPaths.Where(File.Exists))
                {
                    if (new FileInfo(copy).Length != cmd.Length || !File.ReadAllBytes(copy).SequenceEqual(cmd)) continue;
                    File.Delete(copy);
                    Log.Info(Lang.T($"Leftover of an interrupted run removed: {copy} (copy of cmd.exe)", $"Reste d'un lancement interrompu supprimé : {copy} (copie de cmd.exe)"));
                }
            }
            catch (Exception ex) { Log.Warn(Lang.T($"Could not remove the leftovers of the Edge step: {ex.Message}", $"Impossible de supprimer les restes de l'étape Edge : {ex.Message}")); }
        }

        /// <summary>Codes de retour du désinstalleur Chromium/Edge les plus courants (util_constants.h).</summary>
        private static string EdgeExitCode(int code)
        {
            switch (code)
            {
                case 14: return Lang.T(": insufficient rights", " : droits insuffisants");
                case 15: return Lang.T(": Edge is not installed at this (system) level", " : Edge n'est pas installé à ce niveau (système)");
                case 16: return Lang.T(": Edge is running", " : Edge est en cours d'exécution");
                case 19: return Lang.T(": uninstall succeeded", " : désinstallation réussie");
                case 20: return Lang.T(": uninstall failed", " : désinstallation en échec");
                case 21: return Lang.T(": uninstall cancelled", " : désinstallation annulée");
                case 93: return Lang.T(": uninstall refused by Edge (outside the European Economic Area?)", " : désinstallation refusée par Edge (hors Espace économique européen ?)");
                case 532: return Lang.T(": uninstall blocked by Edge for the calling process", " : désinstallation bloquée par Edge pour le processus appelant");
                default: return "";
            }
        }

        private static void LogEdgeContext()
        {
            Log.Info(Lang.T($"Windows: {SystemInfo.WindowsCaption()} (build {SystemInfo.BuildNumber})", $"Windows : {SystemInfo.WindowsCaption()} (build {SystemInfo.BuildNumber})"));
            // Edge n'autorise officiellement sa désinstallation que dans l'Espace économique européen
            object geo = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\International\Geo", "Name", null);
            Log.Info(Lang.T($"Windows region: {geo ?? "unknown"}", $"Région Windows : {geo ?? "inconnue"}"));
            LogEdgeUninstallEntry();
            object policy = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\EdgeUpdateDev", "AllowUninstall", null);
            Log.Info(Lang.T($"EdgeUpdateDev\\AllowUninstall: {policy ?? "missing"}", $"EdgeUpdateDev\\AllowUninstall : {policy ?? "absent"}"));
        }

        private static void LogEdgeUninstallEntry()
        {
            const string path = @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft Edge";
            object version = Registry.GetValue(path, "DisplayVersion", null);
            if (version == null) { Log.Info(Lang.T("“Microsoft Edge” entry missing from installed programs", "Entrée « Microsoft Edge » absente des programmes installés")); return; }
            Log.Info(Lang.T($"Installed programs: Edge {version}, NoRemove={Registry.GetValue(path, "NoRemove", "missing")}", $"Programmes installés : Edge {version}, NoRemove={Registry.GetValue(path, "NoRemove", "absent")}"));
            Log.Info(Lang.T($"UninstallString: {Registry.GetValue(path, "UninstallString", "")}", $"UninstallString : {Registry.GetValue(path, "UninstallString", "")}"));
        }

        private void CopyEdgeInstallerLog(DateTime since)
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var candidates = new[]
            {
                Path.GetTempPath(),
                Path.Combine(windows, "SystemTemp"),
                Path.Combine(windows, "Temp"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\EdgeUpdate\Log"),
            };
            var file = candidates
                .Select(d => Path.Combine(d, "msedge_installer.log"))
                .Where(f => { try { return File.Exists(f) && File.GetLastWriteTime(f) >= since.AddMinutes(-1); } catch (Exception) { return false; } })
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();
            if (file == null) { Log.Warn(Lang.T("Uninstaller log (msedge_installer.log) not found", "Journal du désinstalleur (msedge_installer.log) introuvable")); return; }

            try
            {
                string copy = Path.Combine(_workDir, Lang.T("edge-uninstall.log", "edge-desinstallation.log"));
                File.Copy(file, copy, true);
                Log.Info(Lang.T($"Uninstaller log: {file} (copied to {copy})", $"Journal du désinstalleur : {file} (copié dans {copy})"));
                var lines = File.ReadAllLines(copy);
                var interesting = lines.Where(l => l.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0
                                                || l.IndexOf("WARNING", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                foreach (string l in interesting.Skip(Math.Max(0, interesting.Count - 15))) Log.Warn($"[msedge_installer] {l.Trim()}");
                foreach (string l in lines.Skip(Math.Max(0, lines.Length - 10))) Log.Info($"[msedge_installer] {l.Trim()}");
            }
            catch (Exception ex) { Log.Warn(Lang.T($"Could not read {file}: {ex.Message}", $"Lecture de {file} impossible : {ex.Message}")); }
        }

        private string Taskbar()
        {
            // Disposition de la barre des tâches (format LayoutModification de Microsoft), dans l'ordre d'affichage.
            // « Replace » retire les épingles par défaut de Windows (Edge, Store...).
            const string layout =
@"<?xml version=""1.0"" encoding=""utf-8""?>
<LayoutModificationTemplate
    xmlns=""http://schemas.microsoft.com/Start/2014/LayoutModification""
    xmlns:defaultlayout=""http://schemas.microsoft.com/Start/2014/FullDefaultLayout""
    xmlns:start=""http://schemas.microsoft.com/Start/2014/StartLayout""
    xmlns:taskbar=""http://schemas.microsoft.com/Start/2014/TaskbarLayout""
    Version=""1"">
  <CustomTaskbarLayoutCollection PinListPlacement=""Replace"">
    <defaultlayout:TaskbarLayout>
      <taskbar:TaskbarPinList>
        <taskbar:DesktopApp DesktopApplicationID=""Microsoft.Windows.Explorer"" />
        <taskbar:DesktopApp DesktopApplicationLinkPath=""%ALLUSERSPROFILE%\Microsoft\Windows\Start Menu\Programs\Firefox.lnk"" />
        <taskbar:UWA AppUserModelID=""Microsoft.WindowsTerminal_8wekyb3d8bbwe!App"" />
        <taskbar:DesktopApp DesktopApplicationLinkPath=""%APPDATA%\Microsoft\Windows\Start Menu\Programs\Windows PowerShell\Windows PowerShell.lnk"" />
        <taskbar:UWA AppUserModelID=""windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel"" />
        <taskbar:DesktopApp DesktopApplicationLinkPath=""%APPDATA%\Microsoft\Windows\Start Menu\Programs\System Tools\Control Panel.lnk"" />
      </taskbar:TaskbarPinList>
    </defaultlayout:TaskbarLayout>
  </CustomTaskbarLayoutCollection>
</LayoutModificationTemplate>
";
            // Hors de C:\RoboConfigurator : le fichier doit survivre au nettoyage
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RoboConfigurator");
            string file = Path.Combine(dir, "BarreDesTaches.xml");
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, layout, new UTF8Encoding(false));
            Log.Info(Lang.T($"Taskbar layout written to {file}", $"Disposition de la barre des tâches écrite dans {file}"));
            LogTaskbarSources();

            // Stratégie « Disposition de l'écran de démarrage », côté machine et côté utilisateur : Windows la relit à
            // chaque ouverture de session et l'applique si le fichier est plus récent que la dernière fois
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using (var key = root.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Explorer"))
                {
                    key.SetValue("StartLayoutFile", file, RegistryValueKind.ExpandString);
                    key.SetValue("LockedStartLayout", 1, RegistryValueKind.DWord);
                    Log.Info(Lang.T($"Policy {root.Name}\\...\\Explorer: StartLayoutFile = {key.GetValue("StartLayoutFile")}, LockedStartLayout = {key.GetValue("LockedStartLayout")}",
                                    $"Stratégie {root.Name}\\...\\Explorer : StartLayoutFile = {key.GetValue("StartLayoutFile")}, LockedStartLayout = {key.GetValue("LockedStartLayout")}"));
                }
            }

            string defaultShell = Path.Combine(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                                               @"Default\AppData\Local\Microsoft\Windows\Shell");
            string userShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Shell");
            foreach (string shell in new[] { defaultShell, userShell })
            {
                try
                {
                    Directory.CreateDirectory(shell);
                    File.Copy(file, Path.Combine(shell, "LayoutModification.xml"), true);
                    Log.Info(Lang.T($"Copied to {shell}", $"Copié dans {shell}"));
                }
                catch (Exception ex) { Log.Error(Lang.T($"Copy to {shell}", $"Copie dans {shell}"), ex); }
            }

            // Un profil existant garde ses épingles malgré la stratégie : elles sont sauvegardées puis effacées
            Detail(StepNames.Taskbar, Lang.T("Applying the layout to this session...", "Application de la disposition à cette session..."));
            string pinned = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            Log.Info(Lang.T("Pins before: ", "Épingles avant : ") + PinnedList(pinned));
            ResetTaskbarPins(pinned, Path.Combine(dir, "Sauvegarde-barre-des-taches"));
            RestartExplorer();

            Thread.Sleep(8000);
            string after = PinnedList(pinned);
            Log.Info(Lang.T("Pins after: ", "Épingles après : ") + after);
            string firefox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), @"Programs\Firefox.lnk");
            if (!File.Exists(firefox))
            {
                Log.Warn(Lang.T("Firefox shortcut not found: Firefox will not be pinned", "Raccourci de Firefox introuvable : Firefox ne sera pas épinglé"));
                return Lang.T("Firefox not found: it will not be pinned (Ninite step not done?)", "Firefox introuvable : il ne sera pas épinglé (étape Ninite non faite ?)");
            }
            // Terminal et Paramètres sont des applis du Store : elles n'ont pas de raccourci dans ce dossier
            var missing = new[] { "Firefox", "PowerShell", "Control Panel" }.Where(n => after.IndexOf(n, StringComparison.OrdinalIgnoreCase) < 0).ToList();
            if (missing.Any())
            {
                Log.Warn(Lang.T($"Not pinned yet: {string.Join(", ", missing)}. Windows will apply the layout at the next sign-in (the final restart).",
                                $"Pas encore épinglé : {string.Join(", ", missing)}. Windows appliquera la disposition à la prochaine ouverture de session (le redémarrage de fin)."));
                return Lang.T("Layout saved: it will show after the restart", "Disposition enregistrée : elle s’affichera après le redémarrage");
            }
            Log.Ok(Lang.T("File Explorer, Firefox, Terminal, PowerShell, Settings and Control Panel are pinned", "Explorateur, Firefox, Terminal, PowerShell, Paramètres et Panneau de configuration sont épinglés"));
            return null;
        }

        private static void LogTaskbarSources()
        {
            string Exists(string path) => File.Exists(path) ? Lang.T("found", "trouvé") : Lang.T("MISSING", "ABSENT");
            string firefox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), @"Programs\Firefox.lnk");
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs\Windows PowerShell\Windows PowerShell.lnk");
            string terminal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
            string sep = Lang.T(": ", " : ");
            Log.Info($"  Firefox{sep}{firefox} ({Exists(firefox)})");
            Log.Info($"  PowerShell{sep}{powershell} ({Exists(powershell)})");
            Log.Info($"  Terminal{sep}{terminal} ({Exists(terminal)})");
            string controlPanel = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs\System Tools\Control Panel.lnk");
            Log.Info(Lang.T($"  Control Panel : {controlPanel} ({Exists(controlPanel)})", $"  Panneau de configuration : {controlPanel} ({Exists(controlPanel)})"));
        }

        private static string PinnedList(string folder)
        {
            if (!Directory.Exists(folder)) return Lang.T("(folder missing)", "(dossier absent)");
            var names = Directory.GetFiles(folder, "*.lnk").Select(Path.GetFileNameWithoutExtension).ToList();
            return names.Any() ? string.Join(", ", names) : Lang.T("(none)", "(aucune)");
        }

        private static void ResetTaskbarPins(string pinned, string backup)
        {
            const string taskband = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband";
            try
            {
                Directory.CreateDirectory(backup);
                if (Directory.Exists(pinned))
                    foreach (string lnk in Directory.GetFiles(pinned, "*.lnk"))
                    {
                        File.Copy(lnk, Path.Combine(backup, Path.GetFileName(lnk)), true);
                        File.Delete(lnk);
                    }
                // La clé Taskband contient l'ordre et les épingles d'applis du Store : exportée en .reg avant suppression
                SystemInfo.Run("reg.exe", $"export \"HKCU\\{taskband}\" \"{Path.Combine(backup, "Taskband.reg")}\" /y", timeout: TimeSpan.FromMinutes(2));
                Registry.CurrentUser.DeleteSubKeyTree(taskband, false);
                Log.Info(Lang.T($"Previous pins saved to {backup}, then reset", $"Anciennes épingles sauvegardées dans {backup}, puis effacées"));
            }
            catch (Exception ex) { Log.Error(Lang.T("Resetting the current pins", "Réinitialisation des épingles actuelles"), ex); }
        }

        private static void RestartExplorer()
        {
            Log.Info(Lang.T("Restarting File Explorer...", "Redémarrage de l'explorateur..."));
            foreach (var p in Process.GetProcessesByName("explorer")) { try { p.Kill(); p.WaitForExit(5000); } catch (Exception) { } }
            Thread.Sleep(3000);
            // Windows relance normalement l'explorateur tout seul
            if (!Process.GetProcessesByName("explorer").Any()) Process.Start("explorer.exe");
        }

        private static string OfficeInstalling =>
            Lang.T("Installing Office (this may take a while)...", "Installation d'Office (cela peut prendre un moment)...");

        private const string ClickToRunKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Office\ClickToRun\Configuration";

        // OfficeSetup.exe rend parfois la main avant la fin : Click-to-Run continue avec OfficeC2RClient
        private static readonly string[] OfficeInstallerProcesses = { "OfficeSetup", "OfficeC2RClient" };

        private string Office()
        {
            var word = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft Office\root\Office16\WINWORD.EXE"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft Office\root\Office16\WINWORD.EXE"),
            };
            bool Installed() => word.Any(File.Exists) && Registry.GetValue(ClickToRunKey, "VersionToReport", null) != null;
            string[] Running() => OfficeInstallerProcesses.Where(n => Process.GetProcessesByName(n).Any()).ToArray();
            string State() => Lang.T($"Word {(word.Any(File.Exists) ? "present" : "missing")}, Click-to-Run version {Registry.GetValue(ClickToRunKey, "VersionToReport", null) ?? "missing"}, installer processes: {(Running().Any() ? string.Join(", ", Running()) : "none")}",
                                     $"Word {(word.Any(File.Exists) ? "présent" : "absent")}, version Click-to-Run {Registry.GetValue(ClickToRunKey, "VersionToReport", null) ?? "absente"}, processus d'installation : {(Running().Any() ? string.Join(", ", Running()) : "aucun")}");
            if (Installed()) { Log.Ok(Lang.T($"Office is already installed ({State()})", $"Office est déjà installé ({State()})")); return null; }

            Detail(StepNames.Office, OfficeInstalling);
            Log.Info(OfficeInstalling);
            var start = DateTime.Now;
            var limit = TimeSpan.FromMinutes(60);
            int code = SystemInfo.RunVisible(Path.Combine(_workDir, Payloads.Office.File), "", limit);
            Log.Info(State());
            // Échec immédiat de l'installateur : inutile d'attendre une heure
            if (code != 0 && !Installed() && !Running().Any())
                throw new Exception(Lang.T($"OfficeSetup.exe stopped with code {code} (0x{code:X8}) and Office is not installed",
                                           $"OfficeSetup.exe s'est arrêté avec le code {code} (0x{code:X8}) et Office n'est pas installé"));

            var lastLog = DateTime.Now;
            while (!Installed() || Running().Any())
            {
                var elapsed = DateTime.Now - start;
                if (elapsed > limit)
                {
                    Log.Warn(State());
                    if (!Installed()) throw new Exception(Lang.T("Office does not seem installed after 60 minutes", "Office ne semble pas installé après 60 minutes"));
                    // Installé, mais Click-to-Run travaille encore (mise à jour lancée juste après l'installation ?)
                    return Lang.T("Office is installed but its installer was still running after 60 minutes", "Office est installé mais son installateur tournait encore après 60 minutes");
                }
                if (DateTime.Now - lastLog > TimeSpan.FromMinutes(5)) { Log.Info(Lang.T($"Waiting for Office ({(int)elapsed.TotalMinutes} min): ", $"Attente d'Office ({(int)elapsed.TotalMinutes} min) : ") + State()); lastLog = DateTime.Now; }
                Detail(StepNames.Office, Lang.T($"Installing Office ({(int)elapsed.TotalMinutes} min)...", $"Installation d'Office en cours ({(int)elapsed.TotalMinutes} min)..."));
                Thread.Sleep(15000);
            }
            Log.Ok(Lang.T($"Office installed ({State()})", $"Office installé ({State()})"));
            return null;
        }

        // Clé générique publiée par Microsoft pour passer de Famille à Pro : elle change l'édition mais n'active pas Windows
        private const string GenericProKey = "VK7JG-NPHTM-C97JM-9MPGT-3V66T";

        private string Activation()
        {
            const string key = StepNames.Activation;
            string slmgr = Path.Combine(Environment.SystemDirectory, "slmgr.vbs");
            // /ato contacte les serveurs d'activation de Microsoft : plus long que les autres commandes
            void Slmgr(string args) => SystemInfo.Run("cscript.exe", $"//nologo \"{slmgr}\" {args}", timeout: TimeSpan.FromMinutes(args == "/ato" ? 5 : 2));

            // Édition voulue : Famille -> Pro se fait sur place (redémarrage nécessaire), Pro -> Famille demande une réinstallation
            string edition = SystemInfo.EditionId();
            string wanted = _config.Edition == Editions.Keep ? Lang.T("keep the current one", "garder l'actuelle") : _config.Edition;
            Log.Info(Lang.T($"Installed edition: {edition}; edition wanted: {wanted}", $"Édition installée : {edition} ; édition voulue : {wanted}"));
            string editionWarning = null;
            if (_config.Edition == Editions.Pro && SystemInfo.IsHomeEdition(edition)) UpgradeToPro(key, edition);
            else if (_config.Edition == Editions.Home && SystemInfo.IsProEdition(edition))
            {
                editionWarning = Lang.T("Windows Pro is installed: going back to Home needs a reinstall, Pro is kept",
                                        "Windows Pro est installé : revenir à Famille demande une réinstallation, Pro est conservé");
                Log.Warn(editionWarning);
            }
            else if (_config.Edition != Editions.Keep && !SystemInfo.IsHomeEdition(edition) && !SystemInfo.IsProEdition(edition))
            {
                editionWarning = Lang.T($"Edition {edition} is neither Home nor Pro: it is kept", $"L'édition {edition} n'est ni Famille ni Pro : elle est conservée");
                Log.Warn(editionWarning);
            }

            Log.Info(Lang.T("Licence status before activation:", "État de la licence avant l'activation :"));
            Slmgr("/dli");

            if (!SystemInfo.IsWindowsActivated() && _config.WindowsKey.Length > 0)
            {
                string installing = Lang.T("Installing the Windows key provided...", "Installation de la clé Windows fournie...");
                Detail(key, installing);
                Log.Info(installing);
                Slmgr("/ipk " + _config.WindowsKey);
                Slmgr("/ato");
                Log.Info(Lang.T($"Activated after the key provided: {(SystemInfo.IsWindowsActivated() ? "yes" : "no")}", $"Activé après la clé fournie : {(SystemInfo.IsWindowsActivated() ? "oui" : "non")}"));
            }

            // Une clé OEM Famille ne peut pas activer Windows Pro : elle est alors ignorée
            bool oemFits = _oemKey != null && !(_config.Edition == Editions.Pro && _oemKey.Description.IndexOf("Professional", StringComparison.OrdinalIgnoreCase) < 0);
            if (_oemKey != null && !oemFits)
                Log.Warn(Lang.T($"The BIOS key ({_oemKey.Description}) is not a Pro key: it is not used for Windows Pro",
                                $"La clé du BIOS ({_oemKey.Description}) n'est pas une clé Pro : elle n'est pas utilisée pour Windows Pro"));

            if (!SystemInfo.IsWindowsActivated() && _config.WindowsKey.Length == 0 && oemFits)
            {
                Detail(key, Lang.T("Installing the BIOS Windows key...", "Installation de la clé Windows du BIOS..."));
                Log.Info(Lang.T($"Installing the BIOS Windows key ({_oemKey.Description})...", $"Installation de la clé Windows du BIOS ({_oemKey.Description})..."));
                Slmgr("/ipk " + _oemKey.Key);
                Slmgr("/ato");
            }

            if (SystemInfo.IsWindowsActivated()) { Log.Ok(Lang.T("Windows is activated", "Windows est activé")); return editionWarning; }

            Log.Warn(Lang.T("Windows is not activated: downloading the activation script...", "Windows n'est pas activé : téléchargement du script d'activation..."));
            try
            {
                string script = Payloads.Ensure(Payloads.Mas, _workDir);
                Detail(key, Lang.T("Activating Windows (HWID)...", "Activation de Windows (HWID)..."));
                Log.Info(Lang.T("Starting Windows activation (option 1 - HWID)...", "Lancement de l'activation Windows (option 1 - HWID)..."));
                SystemInfo.RunVisible("cmd.exe", $"/c \"{script}\" /HWID", TimeSpan.FromMinutes(15));
                Detail(key, Lang.T("Activating Office (Ohook)...", "Activation d'Office (Ohook)..."));
                Log.Info(Lang.T("Starting Office activation (option 2 - Ohook)...", "Lancement de l'activation Office (option 2 - Ohook)..."));
                SystemInfo.RunVisible("cmd.exe", $"/c \"{script}\" /Ohook", TimeSpan.FromMinutes(15));
            }
            catch (Exception ex)
            {
                Log.Error(Lang.T("Could not download or run the activation script", "Impossible de télécharger ou lancer le script d'activation"), ex);
                throw new StepNotDoneException(WithEditionWarning(Lang.T("Activation script unavailable: Windows is not activated.", "Script d'activation indisponible : Windows n'est pas activé."), editionWarning));
            }

            if (SystemInfo.IsWindowsActivated()) { Log.Ok(Lang.T("Windows activated successfully", "Windows est activé avec succès")); return editionWarning; }
            Log.Warn(Lang.T("The script ran but Windows does not seem activated. Licence status:", "Le script s'est exécuté mais Windows ne semble pas activé. État de la licence :"));
            Slmgr("/dli");
            throw new StepNotDoneException(WithEditionWarning(Lang.T("Windows does not seem activated.", "Windows ne semble pas activé."), editionWarning));
        }

        private static string WithEditionWarning(string message, string editionWarning) =>
            editionWarning == null ? message : message + " " + editionWarning;

        /// <summary>
        /// Passe Windows Famille à Pro avec changepk.exe (clé fournie, sinon clé générique Pro de Microsoft).
        /// L'édition ne change qu'au redémarrage : l'activation est reprise au lancement suivant.
        /// </summary>
        private void UpgradeToPro(string step, string edition)
        {
            string changepk = Path.Combine(Environment.SystemDirectory, "changepk.exe");
            Detail(step, Lang.T("Switching to Windows Pro...", "Passage à Windows Pro..."));
            var keys = _config.WindowsKey.Length > 0 ? new[] { _config.WindowsKey, GenericProKey } : new[] { GenericProKey };
            int code = -1;
            foreach (string productKey in keys)
            {
                bool generic = productKey == GenericProKey;
                Log.Info(Lang.T($"Switching from {edition} to Windows Pro with {(generic ? "Microsoft's generic Pro key" : "the key provided")} (changepk.exe)...",
                                $"Passage de {edition} à Windows Pro avec {(generic ? "la clé générique Pro de Microsoft" : "la clé fournie")} (changepk.exe)..."));
                if (!File.Exists(changepk)) throw new FileNotFoundException(Lang.T("changepk.exe not found on this Windows", "changepk.exe introuvable sur ce Windows"), changepk);
                code = SystemInfo.Run(changepk, "/ProductKey " + productKey, display: "changepk.exe /ProductKey <" + Lang.T("key", "clé") + ">", timeout: TimeSpan.FromMinutes(15));
                Log.Info(Lang.T($"changepk.exe: code {code} (0x{code:X8}), EditionID now {SystemInfo.EditionId()}", $"changepk.exe : code {code} (0x{code:X8}), EditionID maintenant {SystemInfo.EditionId()}"));
                if (code == 0) break;
            }
            if (code != 0)
                throw new Exception(Lang.T($"Switching to Windows Pro failed (changepk.exe code 0x{code:X8}, see the log)",
                                           $"Le passage à Windows Pro a échoué (code changepk.exe 0x{code:X8}, voir le journal)"));
            throw new RestartRequiredException(Lang.T("Switched to Windows Pro: restart, then run the app again to finish the activation",
                                                      "Passage à Windows Pro fait : redémarrer, puis relancer l’application pour finir l’activation"));
        }

        private void RenamePc()
        {
            if (_stop)
            {
                Set(RenameKey, StepStatus.Skipped, Lang.T("Not applied: stop requested", "Non appliqué : arrêt demandé"));
                return;
            }
            string name = _config.NewName;
            if (string.IsNullOrEmpty(name) || name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                Set(RenameKey, StepStatus.Skipped, Lang.T($"The PC keeps its name ({Environment.MachineName})", $"Le PC garde son nom ({Environment.MachineName})"));
                return;
            }
            Log.Title(Lang.T("PC name", "Nom du PC"));
            _current = RenameKey;
            Set(RenameKey, StepStatus.Running, $"{Environment.MachineName} -> {name}");
            try
            {
                if (!_simulation) SystemInfo.RenameComputer(name);
                _result.PendingName = name;
                string renamed = Lang.T($"The PC will be named “{name}” after restarting", $"Le PC s'appellera « {name} » après le redémarrage");
                Log.Ok(renamed);
                Set(RenameKey, StepStatus.Done, renamed);
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
                Set(RenameKey, StepStatus.Failed, ex.Message);
            }
        }

        private void WriteReport()
        {
            Log.Title(Lang.T("PC report", "Rapport du poste"));
            _current = ReportKey;
            Set(ReportKey, StepStatus.Running, Lang.T("Collecting PC information...", "Collecte des informations du poste..."));
            try
            {
                var status = ReportStatus();
                var lines = Report.Build(_result.PendingName, _oemKey, status);
                string file = Lang.T($"Report-{_result.PendingName ?? Environment.MachineName}-{DateTime.Now:yyyy-MM-dd}.txt", $"Rapport-{_result.PendingName ?? Environment.MachineName}-{DateTime.Now:yyyy-MM-dd}.txt");
                string path = Path.Combine(_workDir, file);
                File.WriteAllLines(path, lines, new UTF8Encoding(true));
                string desktopCopy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), file);
                if (!_simulation) File.Copy(path, desktopCopy, true);
                // La copie du Bureau survit au nettoyage du dossier de travail
                _result.ReportPath = _simulation ? path : desktopCopy;
                Log.Ok(Lang.T($"Report saved{(_simulation ? "" : " to the desktop and")} in {path}", $"Rapport enregistré{(_simulation ? "" : " sur le Bureau et")} dans {path}"));
                Set(ReportKey, StepStatus.Done, _simulation ? path : Lang.T($"Copied to the desktop: {file}", $"Copié sur le Bureau : {file}"));
            }
            catch (Exception ex)
            {
                Log.Error(Lang.T($"Could not create the report: {ex.Message}", $"Impossible de générer le rapport : {ex.Message}"));
                Set(ReportKey, StepStatus.Failed, ex.Message);
            }
        }

        /// <summary>État de chaque étape pour le rapport : le même que dans l'application, avec son détail.</summary>
        private List<string> ReportStatus()
        {
            var lines = new List<string>();
            void Add(string key, string state)
            {
                string detail = _items[key].Detail;
                lines.Add($"{_items[key].Title,-26}{Lang.T(": ", " : ")}{state}{(string.IsNullOrEmpty(detail) ? "" : " - " + detail)}");
            }
            foreach (string s in StepNames.All)
            {
                switch (_items[s].Status)
                {
                    case StepStatus.Skipped: Add(s, Lang.T("skipped", "ignorée")); break;
                    case StepStatus.AlreadyDone: Add(s, Lang.T("OK (previous run)", "OK (lancement précédent)")); break;
                    case StepStatus.Done: Add(s, "OK"); break;
                    case StepStatus.Failed: Add(s, Lang.T("FAILED", "EN ÉCHEC")); break;
                    case StepStatus.Warning:
                        Add(s, _result.NotDone.Contains(s) ? Lang.T("NOT DONE (retried at the next launch)", "NON FAITE (réessayée au prochain lancement)")
                             : _result.AfterRestart.Contains(s) ? Lang.T("TO FINISH after restarting", "À TERMINER après le redémarrage")
                             : Lang.T("OK, TO CHECK", "OK, À VÉRIFIER"));
                        break;
                    default: Add(s, Lang.T("not done", "non faite")); break;
                }
            }
            Add(RenameKey, _items[RenameKey].Status == StepStatus.Failed ? Lang.T("FAILED", "EN ÉCHEC") : _items[RenameKey].Status == StepStatus.Done ? "OK" : Lang.T("unchanged", "inchangé"));
            string keep = CleanUpSkipReason();
            lines.Add($"{_items[CleanUpKey].Title,-26}{Lang.T(": ", " : ")}{keep ?? Lang.T($"{_workDir} deleted after this report", $"{_workDir} supprimé après ce rapport")}");
            return lines;
        }

        /// <summary>Raison de garder le dossier de travail, ou null s'il doit être supprimé.</summary>
        private string CleanUpSkipReason()
        {
            if (!_config.CleanUp) return Lang.T($"Files kept in {_workDir}", $"Fichiers conservés dans {_workDir}");
            if (_stop) return Lang.T("Files kept: the setup was stopped and can be continued", "Fichiers conservés : la configuration a été arrêtée et pourra être poursuivie");
            // Une étape à finir après le redémarrage a besoin de la configuration : jamais de nettoyage dans ce cas
            if (_result.AfterRestart.Any()) return Lang.T("Files kept to finish the setup after restarting", "Fichiers conservés pour finir la configuration après le redémarrage");
            if (_result.HasUnfinished && !_config.CleanUpOnError) return Lang.T("Files kept so the unfinished steps can be resumed", "Fichiers conservés pour pouvoir reprendre les étapes non terminées");
            return null;
        }

        private void CleanUp()
        {
            string keep = CleanUpSkipReason();
            if (keep != null)
            {
                Set(CleanUpKey, StepStatus.Skipped, keep);
                return;
            }
            if (_result.HasUnfinished)
            {
                // Nettoyage forcé : le journal part avec le dossier, une copie reste sur le Bureau pour comprendre l'échec
                string unfinished = StepNames.Display(_result.Failed.Concat(_result.NotDone));
                Log.Warn(Lang.T($"Clean-up forced despite unfinished steps ({unfinished}): they cannot be resumed",
                                $"Nettoyage forcé malgré des étapes non terminées ({unfinished}) : elles ne pourront pas être reprises"));
                CopyLogToDesktop();
            }
            Log.Title(Lang.T("Clean-up", "Nettoyage"));
            _current = CleanUpKey;
            Set(CleanUpKey, StepStatus.Running, Lang.T($"Deleting {_workDir}...", $"Suppression de {_workDir}..."));
            if (_simulation)
            {
                Log.Ok(Lang.T($"The folder {_workDir} would be deleted (simulation)", $"Le dossier {_workDir} serait supprimé (simulation)"));
                Set(CleanUpKey, StepStatus.Done, Lang.T("Working folder deleted (simulation)", "Dossier de travail supprimé (simulation)"));
                return;
            }
            Log.Info(Lang.T($"Deleting the working folder {_workDir} (downloads, settings, log)", $"Suppression du dossier de travail {_workDir} (téléchargements, configuration, journal)"));
            Log.Close();
            try
            {
                int pending = WorkFolder.Delete(_workDir);
                string detail = pending == 0 ? Lang.T($"Folder {_workDir} deleted", $"Dossier {_workDir} supprimé")
                              : Lang.T($"Folder {_workDir} deleted ({pending} file(s) in use will be deleted on restart)",
                                       $"Dossier {_workDir} supprimé ({pending} fichier(s) en cours d'utilisation supprimé(s) au redémarrage)");
                Log.Ok(detail);
                Set(CleanUpKey, StepStatus.Done, detail);
            }
            catch (Exception ex)
            {
                Log.Error(Lang.T("Incomplete clean-up", "Nettoyage incomplet"), ex);
                Set(CleanUpKey, StepStatus.Warning, ex.Message);
            }
        }

        private void CopyLogToDesktop()
        {
            string log = Log.FilePath;
            if (log == null || !File.Exists(log)) return;
            try
            {
                string copy = Path.Combine(_simulation ? _workDir : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                           $"{Path.GetFileNameWithoutExtension(log)}-{Environment.MachineName}-{DateTime.Now:yyyy-MM-dd-HHmm}.txt");
                Log.Info(Lang.T($"Log copied to {copy}", $"Journal copié dans {copy}"));
                File.Copy(log, copy, true);
                _result.LogCopyPath = copy;
            }
            catch (Exception ex) { Log.Error(Lang.T("Copying the log to the desktop", "Copie du journal sur le Bureau"), ex); }
        }

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
                case StepNames.Ninite: actions = new[] { Lang.T("Click “Close” in Ninite when it shows “Done”.", "Cliquez sur « Close » dans Ninite quand il affiche « Done ».") }; break;
                case StepNames.CrapFixer: actions = Enumerable.Range(1, 6).Select(i => Math.Min(i * 8, 47)).Select(n => Lang.T($"Tweak {n}/47", $"Réglage {n}/47")).ToArray(); break;
                case StepNames.Office: actions = new[] { OfficeInstalling }; break;
                case StepNames.Activation:
                    string edition = SystemInfo.EditionId();
                    actions = new[]
                    {
                        _config.Edition == Editions.Pro && SystemInfo.IsHomeEdition(edition) ? Lang.T($"{edition} -> Windows Pro with changepk.exe, then restart", $"{edition} -> Windows Pro avec changepk.exe, puis redémarrage")
                        : _config.Edition == Editions.Home && SystemInfo.IsProEdition(edition) ? Lang.T($"{edition} -> Home: not possible without a reinstall", $"{edition} -> Famille : impossible sans réinstallation")
                        : Lang.T($"Edition {edition} kept", $"Édition {edition} conservée"),
                        Lang.T("Checking the activation...", "Vérification de l'activation..."),
                    };
                    break;
                default: actions = new[] { Lang.T($"{StepNames.Display(step)}: simulation...", $"{StepNames.Display(step)} : simulation...") }; break;
            }
            foreach (string a in actions) { Detail(step, a); Log.Info(a + " (simulation)"); Thread.Sleep(900); }
            return step == StepNames.Activation ? Lang.T("Sample warning: check the activation manually.", "Exemple d'avertissement : vérifier l'activation manuellement.") : null;
        }
    }
}
