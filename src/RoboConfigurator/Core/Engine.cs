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
                var payload = Payloads.ForStep(step);
                if (payload == null) continue;
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
                Log.Title(step);
                Set(step, StepStatus.Running, "");
                var start = DateTime.Now;
                try
                {
                    string warning = _simulation ? SimulateStep(step) : RunStep(step);
                    _state.MarkDone(step);
                    Set(step, warning == null ? StepStatus.Done : StepStatus.Warning, warning ?? Lang.T("Done", "Terminée"));
                    if (warning != null) Log.Warn(Lang.T($"{step} needs checking: {warning}", $"{step} à vérifier : {warning}"));
                    Log.Ok(Lang.T($"{step} done in {(DateTime.Now - start).TotalSeconds:0} s", $"{step} terminé en {(DateTime.Now - start).TotalSeconds:0} s"));
                }
                catch (RestartRequiredException ex)
                {
                    // Pas marquée comme faite : elle sera reprise au prochain lancement, après le redémarrage
                    _result.AfterRestart.Add(step);
                    Set(step, StepStatus.Warning, ex.Message);
                    Log.Warn(Lang.T($"{step} will finish after restarting: {ex.Message}", $"{step} se terminera après le redémarrage : {ex.Message}"));
                }
                catch (Exception ex) { Fail(step, ex, DateTime.Now - start); }
            }

            Log.Title(Lang.T("Summary", "Résumé"));
            if (_result.Failed.Any()) Log.Warn(Lang.T($"Failed steps: {string.Join(", ", _result.Failed)}. Run the app again to retry them.", $"Étapes en échec : {string.Join(", ", _result.Failed)}. Relancer l'application pour les réessayer."));
            if (_result.AfterRestart.Any()) Log.Warn(Lang.T($"To finish after restarting: {string.Join(", ", _result.AfterRestart)}. Run the app again after the restart.", $"À terminer après le redémarrage : {string.Join(", ", _result.AfterRestart)}. Relancer l'application après le redémarrage."));
            if (!_result.Failed.Any() && !_result.AfterRestart.Any()) Log.Ok(Lang.T("All done.", "Tout est terminé."));

            RenamePc();
            WriteReport();

            // Tout est fait : la configuration n'est plus utile (elle peut contenir la clé Windows)
            if (!_result.Failed.Any() && !_result.AfterRestart.Any()) File.Delete(_configFile);
            CleanUp();
            return _result;
        }

        private void LogDiagnostics()
        {
            string Try(Func<string> read) { try { return read(); } catch (Exception ex) { return "? (" + ex.Message + ")"; } }
            string B(bool value) => value ? Lang.T("yes", "oui") : Lang.T("no", "non");
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            Log.Info(Lang.T($"App: version {version}", $"Application : version {version}"));
            Log.Info($"Windows : {Try(SystemInfo.WindowsCaption)} (build {SystemInfo.BuildNumber}, EditionID {Try(SystemInfo.EditionId)}, {(Environment.Is64BitOperatingSystem ? "64" : "32")} bits)");
            Log.Info(Lang.T($"User: {Environment.UserDomainName}\\{Environment.UserName}, administrator: {(SystemInfo.IsAdministrator() ? "yes" : "no")}",
                            $"Utilisateur : {Environment.UserDomainName}\\{Environment.UserName}, administrateur : {(SystemInfo.IsAdministrator() ? "oui" : "non")}"));
            Log.Info(Lang.T($"Language: {System.Globalization.CultureInfo.CurrentUICulture.Name}, region: {System.Globalization.RegionInfo.CurrentRegion.Name}",
                            $"Langue : {System.Globalization.CultureInfo.CurrentUICulture.Name}, région : {System.Globalization.RegionInfo.CurrentRegion.Name}"));
            Log.Info(Try(() =>
            {
                var drive = new DriveInfo(Path.GetPathRoot(_workDir));
                return Lang.T($"Free space on {drive.Name}: {drive.AvailableFreeSpace / (1L << 30)} GB", $"Espace libre sur {drive.Name} : {drive.AvailableFreeSpace / (1L << 30)} Go");
            }));
            Log.Info(Lang.T($"Settings: steps {string.Join(", ", _config.Steps)}; all tweaks {B(_config.AllTweaks)}; edition {(_config.Edition == "" ? "keep" : _config.Edition)}; Windows key {(_config.WindowsKey.Length > 0 ? "provided" : "none")}; restart {B(_config.AutoRestart)}; clean-up {B(_config.CleanUp)} (even on error: {B(_config.CleanUpOnError)})",
                            $"Configuration : étapes {string.Join(", ", _config.Steps)} ; tous les tweaks {B(_config.AllTweaks)} ; édition {(_config.Edition == "" ? "garder" : _config.Edition)} ; clé Windows {(_config.WindowsKey.Length > 0 ? "fournie" : "aucune")} ; redémarrage {B(_config.AutoRestart)} ; nettoyage {B(_config.CleanUp)} (même en cas d'erreur : {B(_config.CleanUpOnError)})"));
        }

        private void Fail(string step, Exception ex, TimeSpan? elapsed = null)
        {
            string after = elapsed == null ? "" : Lang.T($" after {elapsed.Value.TotalSeconds:0} s", $" après {elapsed.Value.TotalSeconds:0} s");
            Log.Error(Lang.T($"{step} failed{after}", $"{step} en échec{after}"), ex);
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
            Log.Info(Lang.T("Ninite installs the software automatically.", "Ninite installe les logiciels automatiquement."));
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
            // Même méthode que WinUtil (Chris Titus) : un faux MicrosoftEdge.exe dans l'ancien dossier
            // d'Edge débloque le désinstalleur officiel. WebView2 (utilisé par Office, Teams...) est conservé.
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

            string fake = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"SystemApps\Microsoft.MicrosoftEdge_8wekyb3d8bbwe\MicrosoftEdge.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(fake));
            File.WriteAllBytes(fake, new byte[0]);
            Log.Info(Lang.T($"Dummy MicrosoftEdge.exe created: {fake}", $"Faux MicrosoftEdge.exe créé : {fake}"));

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

            Detail(StepNames.Edge, Lang.T("Uninstalling Microsoft Edge...", "Désinstallation de Microsoft Edge..."));
            const string args = "--uninstall --msedge --channel=stable --system-level --verbose-logging --force-uninstall --delete-profile";
            Log.Info(Lang.T($"Command: \"{setup}\" {args}", $"Commande : \"{setup}\" {args}"));
            var start = DateTime.Now;
            int code = SystemInfo.Run(setup, args);
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

            // Le compte actuel a déjà ses épingles : Windows les garde tant qu'elles ne sont pas réinitialisées.
            // Elles sont sauvegardées, effacées, puis l'explorateur est relancé pour appliquer la disposition tout de suite.
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
            var missing = new[] { "Firefox", "PowerShell" }.Where(n => after.IndexOf(n, StringComparison.OrdinalIgnoreCase) < 0).ToList();
            if (missing.Any())
            {
                Log.Warn(Lang.T($"Not pinned yet: {string.Join(", ", missing)}. Windows will apply the layout at the next sign-in (the final restart).",
                                $"Pas encore épinglé : {string.Join(", ", missing)}. Windows appliquera la disposition à la prochaine ouverture de session (le redémarrage de fin)."));
                return Lang.T("Layout saved: it will show after the restart", "Disposition enregistrée : elle s’affichera après le redémarrage");
            }
            Log.Ok(Lang.T("File Explorer, Firefox, Terminal and PowerShell are pinned", "Explorateur, Firefox, Terminal et PowerShell sont épinglés"));
            return null;
        }

        private static void LogTaskbarSources()
        {
            string Exists(string path) => File.Exists(path) ? Lang.T("found", "trouvé") : Lang.T("MISSING", "ABSENT");
            string firefox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), @"Programs\Firefox.lnk");
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs\Windows PowerShell\Windows PowerShell.lnk");
            string terminal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
            Log.Info($"  Firefox : {firefox} ({Exists(firefox)})");
            Log.Info($"  PowerShell : {powershell} ({Exists(powershell)})");
            Log.Info($"  Terminal : {terminal} ({Exists(terminal)})");
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
                SystemInfo.Run("reg.exe", $"export \"HKCU\\{taskband}\" \"{Path.Combine(backup, "Taskband.reg")}\" /y");
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

        private string Office()
        {
            var word = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft Office\root\Office16\WINWORD.EXE"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft Office\root\Office16\WINWORD.EXE"),
            };
            bool Installed() => word.Any(File.Exists);
            if (Installed()) { Log.Ok(Lang.T("Office is already installed", "Office est déjà installé")); return null; }

            Detail(StepNames.Office, OfficeInstalling);
            Log.Info(OfficeInstalling);
            var start = DateTime.Now;
            SystemInfo.RunVisible(Path.Combine(_workDir, Payloads.Office.File));
            // Le programme d'installation peut rendre la main avant la fin réelle
            while (!Installed() || Process.GetProcessesByName("OfficeSetup").Any())
            {
                var elapsed = DateTime.Now - start;
                if (elapsed.TotalMinutes > 60) throw new Exception(Lang.T("Office does not seem installed after 60 minutes", "Office ne semble pas installé après 60 minutes"));
                Detail(StepNames.Office, Lang.T($"Installing Office ({(int)elapsed.TotalMinutes} min)...", $"Installation d'Office en cours ({(int)elapsed.TotalMinutes} min)..."));
                Thread.Sleep(15000);
            }
            Log.Ok(Lang.T("Office installed", "Office installé"));
            return null;
        }

        // Clé générique publiée par Microsoft pour passer de Famille à Pro : elle change l'édition mais n'active pas Windows
        private const string GenericProKey = "VK7JG-NPHTM-C97JM-9MPGT-3V66T";

        private string Activation()
        {
            const string key = StepNames.Activation;
            string slmgr = Path.Combine(Environment.SystemDirectory, "slmgr.vbs");
            void Slmgr(string args) => SystemInfo.Run("cscript.exe", $"//nologo \"{slmgr}\" {args}");

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
                SystemInfo.RunVisible("cmd.exe", $"/c \"{script}\" /HWID");
                Detail(key, Lang.T("Activating Office (Ohook)...", "Activation d'Office (Ohook)..."));
                Log.Info(Lang.T("Starting Office activation (option 2 - Ohook)...", "Lancement de l'activation Office (option 2 - Ohook)..."));
                SystemInfo.RunVisible("cmd.exe", $"/c \"{script}\" /Ohook");
            }
            catch (Exception ex)
            {
                Log.Error(Lang.T("Could not download or run the activation script", "Impossible de télécharger ou lancer le script d'activation"), ex);
                return Lang.T("Activation script unavailable: activate Windows manually.", "Script d'activation indisponible : activer Windows manuellement.");
            }

            if (SystemInfo.IsWindowsActivated()) { Log.Ok(Lang.T("Windows activated successfully", "Windows est activé avec succès")); return editionWarning; }
            Log.Warn(Lang.T("The script ran but Windows does not seem activated. Licence status:", "Le script s'est exécuté mais Windows ne semble pas activé. État de la licence :"));
            Slmgr("/dli");
            return Lang.T("Windows does not seem activated: check manually.", "Windows ne semble pas activé : vérifier manuellement.");
        }

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
                code = SystemInfo.Run(changepk, "/ProductKey " + productKey, display: "changepk.exe /ProductKey <" + Lang.T("key", "clé") + ">");
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
            string name = _config.NewName;
            if (string.IsNullOrEmpty(name) || name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                Set(RenameKey, StepStatus.Skipped, Lang.T($"The PC keeps its name ({Environment.MachineName})", $"Le PC garde son nom ({Environment.MachineName})"));
                return;
            }
            Log.Title(Lang.T("PC name", "Nom du PC"));
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
            Set(ReportKey, StepStatus.Running, Lang.T("Collecting PC information...", "Collecte des informations du poste..."));
            try
            {
                var status = StepNames.All.Select(s =>
                {
                    string state = !_config.Has(s) ? Lang.T("skipped", "ignorée")
                                 : _result.Failed.Contains(s) ? Lang.T("FAILED", "EN ÉCHEC")
                                 : _result.AfterRestart.Contains(s) ? Lang.T("to finish after restarting", "à terminer après le redémarrage")
                                 : _state.IsDone(s) ? "OK"
                                 : Lang.T("not done", "non faite");
                    return $"{s,-15}{Lang.T(": ", " : ")}{state}";
                });
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

        private void CleanUp()
        {
            if (!_config.CleanUp)
            {
                Set(CleanUpKey, StepStatus.Skipped, Lang.T($"Files kept in {_workDir}", $"Fichiers conservés dans {_workDir}"));
                return;
            }
            // Une étape à finir après le redémarrage a besoin de la configuration : jamais de nettoyage dans ce cas
            if (_result.AfterRestart.Any())
            {
                Set(CleanUpKey, StepStatus.Skipped, Lang.T("Files kept to finish the setup after restarting", "Fichiers conservés pour finir la configuration après le redémarrage"));
                return;
            }
            if (_result.Failed.Any() && !_config.CleanUpOnError)
            {
                Set(CleanUpKey, StepStatus.Skipped, Lang.T("Files kept so the failed steps can be resumed", "Fichiers conservés pour pouvoir reprendre les étapes en échec"));
                return;
            }
            if (_result.Failed.Any())
            {
                // Nettoyage forcé : le journal part avec le dossier, une copie reste sur le Bureau pour comprendre l'échec
                Log.Warn(Lang.T($"Clean-up forced despite failed steps ({string.Join(", ", _result.Failed)}): they cannot be resumed",
                                $"Nettoyage forcé malgré des étapes en échec ({string.Join(", ", _result.Failed)}) : elles ne pourront pas être reprises"));
                CopyLogToDesktop();
            }
            Log.Title(Lang.T("Clean-up", "Nettoyage"));
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
                int pending = DeleteFolder(_workDir);
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
                default: actions = new[] { Lang.T($"{step}: simulation...", $"{step} : simulation...") }; break;
            }
            foreach (string a in actions) { Detail(step, a); Log.Info(a + " (simulation)"); Thread.Sleep(900); }
            return step == StepNames.Activation ? Lang.T("Sample warning: check the activation manually.", "Exemple d'avertissement : vérifier l'activation manuellement.") : null;
        }
    }
}
