using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using RoboConfigurator.Core;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RoboConfigurator
{
    public partial class MainWindow : FluentWindow
    {
        private readonly Options _options;
        private readonly string _workDir;
        private readonly string _configFile;
        private readonly string _stateFile;
        private readonly StepState _state;
        private readonly List<StepItem> _steps;
        private readonly List<StepItem> _runItems;
        private readonly Dictionary<string, StepItem> _items;
        private readonly List<AppItem> _apps;
        private UpdateInfo _update;

        /// <summary>log.txt ou journal.txt selon la langue ; une reprise dans l'autre langue continue le journal existant.</summary>
        private string LogFile
        {
            get
            {
                string wanted = Path.Combine(_workDir, Lang.T("log.txt", "journal.txt"));
                string other = Path.Combine(_workDir, Lang.T("journal.txt", "log.txt"));
                return !File.Exists(wanted) && File.Exists(other) ? other : wanted;
            }
        }

        private OemKey _oemKey;
        private bool _running;
        private bool _restartPending;
        private RunResult _result;
        private Engine _engine;
        private DispatcherTimer _restartTimer;

        public MainWindow(Options options)
        {
            _options = options;
            InitializeComponent();
            SystemThemeWatcher.Watch(this);
            FitToScreen();

            _workDir = options.Simulation
                ? Path.Combine(Path.GetTempPath(), "RoboConfigurator-simulation")
                : Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "RoboConfigurator");
            Directory.CreateDirectory(_workDir);
            // Avant toute écriture : la configuration peut contenir la clé Windows
            string permissions = options.Simulation ? null : WorkFolder.Secure(_workDir);
            _configFile = Path.Combine(_workDir, "configuration.json");
            _stateFile = Path.Combine(_workDir, "etapes-terminees.txt");
            if (options.Reset) ForgetPreviousRun();
            _state = new StepState(_stateFile);
            Log.Open(LogFile);
            if (permissions != null) Log.Info(permissions);
            if (!options.Simulation) Engine.RemoveEdgeLeftovers();
            Log.Written += OnLog;

            _steps = new List<StepItem>
            {
                new StepItem(StepNames.Ninite, "Ninite", Lang.T("Installs 7-Zip, Chrome, Firefox, Foxit Reader and Notepad++. Click “Close” at the end.", "Installe 7-Zip, Chrome, Firefox, Foxit Reader et Notepad++. Cliquer sur « Close » à la fin."), SymbolRegular.AppsAddIn24),
                new StepItem(StepNames.CrapFixer, "CrapFixer", Lang.T("Applies the recommended tweaks and dark theme, removes bloatware (Candy Crush, Bing News...).", "Applique les tweaks recommandés et le thème sombre, supprime les applis inutiles (Candy Crush, Bing News...)."), SymbolRegular.Broom24),
                new StepItem(StepNames.Edge, StepNames.Display(StepNames.Edge), Lang.T("Edge's official uninstaller, started so that Windows 11 24H2 does not block it. WebView2 is kept.", "Désinstalleur officiel d'Edge, lancé de façon à ne pas être bloqué par Windows 11 24H2. WebView2 est conservé."), SymbolRegular.Globe24),
                new StepItem(StepNames.Taskbar, StepNames.Display(StepNames.Taskbar), Lang.T("Pins, in this order: File Explorer, Firefox, Terminal, PowerShell, Settings, Control Panel (replaces the Windows pins). Applied at next sign-in.", "Épingle dans cet ordre : Explorateur, Firefox, Terminal, PowerShell, Paramètres, Panneau de configuration (remplace les épingles de Windows). Appliqué à la prochaine ouverture de session."), SymbolRegular.Pin24),
                new StepItem(StepNames.Office, StepNames.Display(StepNames.Office), Lang.T("Installs Office and waits for the installation to finish.", "Installe Office et attend la fin de l'installation."), SymbolRegular.Document24),
                new StepItem(StepNames.Activation, StepNames.Display(StepNames.Activation), Lang.T("If Windows is not activated: the key entered, otherwise the BIOS OEM key, otherwise the activation script.", "Si Windows n'est pas activé : clé saisie, sinon clé OEM du BIOS, sinon script d'activation."), SymbolRegular.ShieldCheckmark24),
            };
            _runItems = _steps.Concat(new[]
            {
                new StepItem(Engine.RenameKey, Lang.T("PC name", "Nom du PC"), "", SymbolRegular.Rename24),
                new StepItem(Engine.ReportKey, Lang.T("PC report", "Rapport du poste"), "", SymbolRegular.DocumentBulletList24),
                new StepItem(Engine.CleanUpKey, Lang.T("Clean-up", "Nettoyage"), "", SymbolRegular.Delete24),
            }).ToList();
            _items = _runItems.ToDictionary(s => s.Key);
            foreach (var item in _runItems) item.PropertyChanged += OnStepChanged;
            foreach (var step in _steps) step.AlreadyDone = _state.IsDone(step.Key);
            StepsList.ItemsSource = _steps;
            RunList.ItemsSource = _runItems;

            _apps = NiniteApps.All.Select(a => new AppItem(a)).ToList();
            foreach (var app in _apps) app.PropertyChanged += (s, e) => UpdateAppsSummary();
            var appsView = new ListCollectionView(_apps);
            appsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AppItem.Category)));
            AppsList.ItemsSource = appsView;
            ShowPage(PostePageIndex);

            PcNameText.Text = Environment.MachineName;
            NewNameBox.PlaceholderText = Environment.MachineName;
            if (options.Simulation) SimulationBadge.Visibility = Visibility.Visible;
            LoadConfig();

            Loaded += async (s, e) => await Task.WhenAll(LoadSystemInfoAsync(), CheckForUpdateAsync());
            Closing += OnClosing;
        }

        /// <summary>
        /// La fenêtre fait 980 × 820 : en 1366 × 768, ou en 1024 × 768 avec le pilote graphique de base juste après l'installation
        /// de Windows, la barre du bas (bouton Démarrer) sortirait de l'écran. Elle est alors ajustée à la zone de travail et agrandie.
        /// </summary>
        private void FitToScreen()
        {
            var area = SystemParameters.WorkArea;
            if (Width <= area.Width && Height <= area.Height) return;
            MinWidth = Math.Min(MinWidth, area.Width);
            MinHeight = Math.Min(MinHeight, area.Height);
            Width = Math.Min(Width, area.Width);
            Height = Math.Min(Height, area.Height);
            WindowState = WindowState.Maximized;
        }

        private void LoadConfig()
        {
            RunConfig config = null;
            if (File.Exists(_configFile))
            {
                try
                {
                    config = RunConfig.Load(_configFile);
                    ShowMessage(ConfigMessage, InfoBarSeverity.Informational, Lang.T("Resuming the previous run", "Reprise du lancement précédent"),
                        Lang.T("The settings of the interrupted run were reloaded. Completed steps will be skipped.", "La configuration du lancement interrompu a été rechargée. Les étapes déjà faites seront passées."));
                }
                catch (Exception ex)
                {
                    string aside = null;
                    try { aside = RunConfig.SetAsideUnreadable(_configFile); } catch (Exception) { }
                    Log.Warn(Lang.T($"Previous settings could not be read ({ex.Message}); kept as {aside ?? _configFile}",
                                    $"Configuration précédente illisible ({ex.Message}) ; gardée sous {aside ?? _configFile}"));
                    ShowMessage(ConfigMessage, InfoBarSeverity.Warning, Lang.T("Previous settings could not be read", "Configuration précédente illisible"),
                        Lang.T($"The default settings are shown: check them before starting. Completed steps are still skipped. Damaged file: {aside ?? _configFile}",
                               $"La configuration par défaut est affichée : vérifiez-la avant de démarrer. Les étapes déjà faites restent passées. Fichier abîmé : {aside ?? _configFile}"));
                }
            }
            config = config ?? new RunConfig
            {
                Steps = StepNames.All.Except(_options.Skip).ToArray(),
                AllTweaks = _options.AllTweaks,
                WindowsKey = _options.WindowsKey,
                Edition = _options.Edition,
                CleanUpOnError = _options.ForceCleanUp,
                NiniteApps = _options.Apps,
            };
            if (_options.Errors.Any())
                ShowMessage(ConfigMessage, InfoBarSeverity.Error, Lang.T("Command-line options ignored", "Options de lancement ignorées"), string.Join("\n", _options.Errors));

            NewNameBox.Text = config.NewName;
            foreach (var step in _steps) step.IsSelected = config.Has(step.Key);
            TweaksBox.SelectedIndex = config.AllTweaks ? 1 : 0;
            KeyBox.Text = config.WindowsKey;
            RestartSwitch.IsChecked = config.AutoRestart;
            CleanUpSwitch.IsChecked = config.CleanUp;
            CleanUpOnErrorSwitch.IsChecked = config.CleanUpOnError;
            EditionBox.SelectedIndex = Array.IndexOf(EditionValues, config.Edition) is int i && i >= 0 ? i : 0;
            SelectApps(config.NiniteApps);
        }

        private const int PostePageIndex = 0, AppsPageIndex = 1, OptionsPageIndex = 2;

        private void OnNavChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Appelé pendant InitializeComponent, avant que les pages existent
            if (PostePage == null) return;
            int page = NavList.SelectedIndex;
            PostePage.Visibility = page == PostePageIndex ? Visibility.Visible : Visibility.Collapsed;
            AppsPage.Visibility = page == AppsPageIndex ? Visibility.Visible : Visibility.Collapsed;
            OptionsPage.Visibility = page == OptionsPageIndex ? Visibility.Visible : Visibility.Collapsed;
            switch (page)
            {
                case AppsPageIndex:
                    PageTitleText.Text = "Applications";
                    PageSubtitleText.Text = Lang.T("Apps installed by the Ninite step. The default selection is already checked: add or remove apps as needed.",
                                                   "Applis installées par l’étape Ninite. La sélection par défaut est déjà cochée : ajoutez-en ou retirez-en selon le besoin.");
                    break;
                case OptionsPageIndex:
                    PageTitleText.Text = "Options";
                    PageSubtitleText.Text = Lang.T("Settings used by the steps and at the end of the setup.", "Réglages utilisés par les étapes et à la fin de la configuration.");
                    break;
                default:
                    PageTitleText.Text = Lang.T("Configure this PC", "Configuration du poste");
                    PageSubtitleText.Text = Lang.T("Set everything up here, then click Start: the setup then runs on its own until the end.",
                                                   "Réglez tout ici puis cliquez sur Démarrer : la configuration se déroule ensuite seule jusqu’à la fin.");
                    break;
            }
            PageScroll.ScrollToTop();
        }

        private void ShowPage(int page)
        {
            if (NavList.SelectedIndex == page) OnNavChanged(NavList, null);
            else NavList.SelectedIndex = page;
        }

        private void SelectApps(IEnumerable<string> slugs)
        {
            var selected = new HashSet<string>(NiniteApps.Normalize(slugs));
            foreach (var app in _apps) app.IsSelected = selected.Contains(app.App.Slug);
            UpdateAppsSummary();
        }

        private string[] SelectedApps => _apps.Where(a => a.IsSelected).Select(a => a.App.Slug).ToArray();

        private void UpdateAppsSummary()
        {
            var apps = SelectedApps;
            AppsCountText.Text = Lang.T($"{apps.Length} app(s) selected", $"{apps.Length} appli(s) sélectionnée(s)");
            _items[StepNames.Ninite].Description = apps.Length == 0
                ? Lang.T("No app selected: choose them in the Applications menu.", "Aucune appli choisie : à choisir dans le menu Applications.")
                : Lang.T($"Installs {NiniteApps.Names(apps)}. Click “Close” at the end.", $"Installe {NiniteApps.Names(apps)}. Cliquer sur « Close » à la fin.");
        }

        private void OnAppsDefaultClick(object sender, RoutedEventArgs e) => SelectApps(NiniteApps.Default);

        private void OnAppsNoneClick(object sender, RoutedEventArgs e) => SelectApps(new string[0]);

        private async Task CheckForUpdateAsync()
        {
            _update = await Updates.CheckAsync();
            if (_update == null) return;
            UpdateTitle.Text = Lang.T($"Version {_update.Version} is available (this exe: {Updates.Current})", $"La version {_update.Version} est disponible (cet exe : {Updates.Current})");
            UpdateBanner.Visibility = Visibility.Visible;
        }

        private void OnUpdateDownloadClick(object sender, RoutedEventArgs e) => OpenUrl(_update?.DownloadUrl);

        private void OnUpdateNotesClick(object sender, RoutedEventArgs e) => OpenUrl(_update?.PageUrl);

        private static void OpenUrl(string url)
        {
            if (Updates.IsTrustedUrl(url)) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else Log.Warn(Lang.T($"Update link ignored (not on github.com): {url}", $"Lien de mise à jour ignoré (pas sur github.com) : {url}"));
        }

        // Même ordre que les choix de EditionBox
        private static readonly string[] EditionValues = { Editions.Keep, Editions.Pro, Editions.Home };

        private async Task LoadSystemInfoAsync()
        {
            string Try(Func<string> read) { try { return read(); } catch (Exception) { return ""; } }
            bool? TryBool(Func<bool> read) { try { return read(); } catch (Exception) { return null; } }
            var load = Task.Run(() => new
            {
                Model = Try(SystemInfo.Model),
                Windows = Try(SystemInfo.WindowsCaption),
                Activated = TryBool(SystemInfo.IsWindowsActivated),
                Oem = SystemInfo.GetOemKey(),
                SessionUser = Try(SystemInfo.SessionUser),
            });
            // Un service WMI abîmé peut ne jamais répondre : le bouton Démarrer ne doit pas rester grisé pour autant
            if (await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(20))) != load)
            {
                Log.Warn(Lang.T("The PC information (WMI) did not answer within 20 s: the BIOS key will not be used",
                                "Les informations du PC (WMI) n'ont pas répondu en 20 s : la clé du BIOS ne sera pas utilisée"));
                PcModelText.Text = Lang.T("PC information unavailable", "Informations du PC indisponibles");
                SetActivation(SymbolRegular.ShieldQuestion24, "TextFillColorSecondaryBrush", Lang.T("Activation unknown", "Activation inconnue"));
                StartButton.IsEnabled = true;
                return;
            }
            var info = load.Result;

            PcModelText.Text = info.Model;
            WindowsText.Text = info.Windows;
            if (info.Activated == true) SetActivation(SymbolRegular.ShieldCheckmark24, "SystemFillColorSuccessBrush", Lang.T("Windows is activated", "Windows est activé"));
            else if (info.Activated == false) SetActivation(SymbolRegular.ShieldError24, "SystemFillColorCautionBrush", Lang.T("Windows is not activated", "Windows n'est pas activé"));
            else SetActivation(SymbolRegular.ShieldQuestion24, "TextFillColorSecondaryBrush", Lang.T("Activation unknown", "Activation inconnue"));

            _oemKey = info.Oem;
            if (_oemKey != null)
                KeyHint.Text = Lang.T($"BIOS key detected ({_oemKey.Description}): used automatically if no key is entered.",
                                      $"Clé du BIOS détectée ({_oemKey.Description}) : utilisée automatiquement si aucune clé n'est saisie.");

            // Lancée avec le compte administrateur d'un autre utilisateur (UAC) : HKCU, la barre des tâches et le Bureau
            // seraient ceux de ce compte, pas ceux de la personne connectée
            if (!string.IsNullOrEmpty(info.SessionUser) && !info.SessionUser.Equals(SystemInfo.ProcessUser, StringComparison.OrdinalIgnoreCase))
            {
                string warning = Lang.T($"The app runs as {SystemInfo.ProcessUser} but the open session is {info.SessionUser}: the tweaks for the user, the taskbar and the desktop report will apply to {SystemInfo.ProcessUser}. Sign in with the account to set up, then run the app again.",
                                        $"L'application tourne avec {SystemInfo.ProcessUser} mais la session ouverte est celle de {info.SessionUser} : les réglages de l'utilisateur, la barre des tâches et le rapport du Bureau s'appliqueront à {SystemInfo.ProcessUser}. Ouvrez la session du compte à configurer, puis relancez l'application.");
                Log.Warn(warning);
                if (!ConfigMessage.IsOpen || ConfigMessage.Severity != InfoBarSeverity.Error)
                    ShowMessage(ConfigMessage, InfoBarSeverity.Warning, Lang.T("Different account", "Compte différent"), warning);
            }
            StartButton.IsEnabled = true;
        }

        private void SetActivation(SymbolRegular symbol, string brushKey, string text)
        {
            ActivationIcon.Symbol = symbol;
            ActivationIcon.SetResourceReference(IconElement.ForegroundProperty, brushKey);
            ActivationText.Text = text;
        }

        private static void ShowMessage(InfoBar bar, InfoBarSeverity severity, string title, string message)
        {
            bar.Severity = severity;
            bar.Title = title;
            bar.Message = message;
            bar.IsOpen = true;
        }

        private void ForgetPreviousRun()
        {
            File.Delete(_stateFile);
            File.Delete(_configFile);
        }

        private void OnResetClick(object sender, RoutedEventArgs e)
        {
            ForgetPreviousRun();
            foreach (var step in _steps) { step.AlreadyDone = false; step.IsSelected = true; }
            NewNameBox.Text = "";
            KeyBox.Text = "";
            TweaksBox.SelectedIndex = 0;
            RestartSwitch.IsChecked = true;
            CleanUpSwitch.IsChecked = true;
            CleanUpOnErrorSwitch.IsChecked = false;
            EditionBox.SelectedIndex = 0;
            SelectApps(NiniteApps.Default);
            ShowMessage(ConfigMessage, InfoBarSeverity.Success, Lang.T("Everything will be redone", "Tout sera refait"),
                Lang.T("Completed steps and the saved settings have been forgotten.", "Les étapes déjà faites et la configuration enregistrée ont été oubliées."));
        }

        private async void OnStartClick(object sender, RoutedEventArgs e)
        {
            string name = NewNameBox.Text.Trim().ToUpperInvariant();
            if (name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) name = "";
            if (name.Length > 0 && !RunConfig.IsValidPcName(name))
            {
                ShowPage(PostePageIndex);
                ShowMessage(ConfigMessage, InfoBarSeverity.Error, Lang.T("Invalid PC name", "Nom du PC invalide"),
                    Lang.T("1 to 15 characters: letters, digits and hyphens, not digits only.", "1 à 15 caractères : lettres, chiffres et tirets, pas uniquement des chiffres."));
                return;
            }
            string key = KeyBox.Text.Trim().ToUpperInvariant();
            if (key.Length > 0 && !RunConfig.IsValidKey(key))
            {
                ShowPage(OptionsPageIndex);
                ShowMessage(ConfigMessage, InfoBarSeverity.Error, Lang.T("Invalid Windows key", "Clé Windows invalide"), Lang.T("Expected format: XXXXX-XXXXX-XXXXX-XXXXX-XXXXX.", "Format attendu : XXXXX-XXXXX-XXXXX-XXXXX-XXXXX."));
                return;
            }
            if (_items[StepNames.Ninite].IsSelected && SelectedApps.Length == 0)
            {
                ShowPage(AppsPageIndex);
                ShowMessage(ConfigMessage, InfoBarSeverity.Error, Lang.T("No app selected for Ninite", "Aucune appli choisie pour Ninite"),
                    Lang.T("Check at least one app, or turn off the Ninite step.", "Cochez au moins une appli, ou désactivez l’étape Ninite."));
                return;
            }

            var config = new RunConfig
            {
                NewName = name,
                Steps = _steps.Where(s => s.IsSelected).Select(s => s.Key).ToArray(),
                AllTweaks = TweaksBox.SelectedIndex == 1,
                WindowsKey = key,
                AutoRestart = RestartSwitch.IsChecked == true,
                CleanUp = CleanUpSwitch.IsChecked == true,
                CleanUpOnError = CleanUpSwitch.IsChecked == true && CleanUpOnErrorSwitch.IsChecked == true,
                Edition = EditionValues[Math.Max(0, EditionBox.SelectedIndex)],
                NiniteApps = SelectedApps,
            };

            var confirm = new Wpf.Ui.Controls.MessageBox
            {
                Title = Lang.T("Start the setup?", "Démarrer la configuration ?"),
                Content = config.AutoRestart
                    ? Lang.T("The PC will be set up automatically, then restart at the end.", "Le PC va être configuré automatiquement, puis redémarrera à la fin.")
                    : Lang.T("The PC will be set up automatically until the end.", "Le PC va être configuré automatiquement jusqu'à la fin."),
                PrimaryButtonText = Lang.T("Start", "Démarrer"),
                CloseButtonText = Lang.T("Cancel", "Annuler"),
            };
            // Bouton désactivé pendant la question : un double-clic lancerait deux configurations
            StartButton.IsEnabled = false;
            bool confirmed = await confirm.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
            StartButton.IsEnabled = true;
            if (!confirmed) return;

            config.Save(_configFile);
            ConfigView.Visibility = Visibility.Collapsed;
            RunView.Visibility = Visibility.Visible;
            OverallProgress.Maximum = _runItems.Count;

            _engine = new Engine(config, _workDir, _configFile, _state, _oemKey, _options.Simulation, _items) { SimulatedFailures = _options.SimulateFail, CheckLinksInSimulation = true };
            _running = true;
            try { _result = await Task.Run(_engine.Run); }
            catch (Exception ex)
            {
                // Engine.Run ne lève pas d'exception : filet de sécurité pour toujours afficher l'écran de fin
                Log.Error(Lang.T("Unexpected error", "Erreur inattendue"), ex);
                _result = new RunResult { UnexpectedError = ex.GetBaseException().Message };
            }
            finally { _running = false; }
            ShowSummary(config);
        }

        private void OnStepChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(StepItem.Status)) return;
            var item = (StepItem)sender;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                OverallProgress.Value = _runItems.Count(i => i.Status != StepStatus.Pending && !i.IsBusy);
                if (item.Key != StepNames.Ninite) return;
                if (item.Status == StepStatus.Running)
                    ShowMessage(RunMessage, InfoBarSeverity.Informational, Lang.T("Action required", "Action requise"),
                        Lang.T("When Ninite shows “Done”, click “Close” to continue.", "Quand Ninite affiche « Done », cliquez sur « Close » pour passer à la suite."));
                else RunMessage.IsOpen = false;
            }));
        }

        private void OnLog(LogEntry entry)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LogBox.AppendText(entry + Environment.NewLine);
                LogBox.ScrollToEnd();
            }));
        }

        private void ShowSummary(RunConfig config)
        {
            OverallProgress.Value = OverallProgress.Maximum;
            StopButton.Visibility = Visibility.Collapsed;
            var warnings = _runItems.Where(s => s.Status == StepStatus.Warning).ToList();
            if (_result.UnexpectedError != null)
            {
                RunTitle.Text = Lang.T("The setup stopped", "La configuration s'est arrêtée");
                ShowMessage(RunMessage, InfoBarSeverity.Error, Lang.T("Unexpected error: ", "Erreur inattendue : ") + _result.UnexpectedError,
                    Lang.T("The details are in the log. Run the app again to resume: the settings are kept and successful steps will not be redone.",
                           "Le détail est dans le journal. Relancez l'application pour reprendre : la configuration est conservée et les étapes réussies ne seront pas refaites."));
            }
            else if (_result.StopRequested)
            {
                RunTitle.Text = Lang.T("Setup stopped", "Configuration arrêtée");
                ShowMessage(RunMessage, InfoBarSeverity.Warning, Lang.T("Stopped at your request", "Arrêtée à votre demande"),
                    (_result.Stopped.Any() ? Lang.T("Not started: ", "Non lancées : ") + string.Join(", ", _result.Stopped.Select(s => _items[s].Title)) + "\n" : "") +
                    Lang.T("Run the app again to continue: the settings are kept and completed steps will not be redone.",
                           "Relancez l'application pour continuer : la configuration est conservée et les étapes faites ne seront pas refaites."));
            }
            else if (_result.Failed.Any())
            {
                RunTitle.Text = Lang.T("Setup finished with errors", "Configuration terminée avec des erreurs");
                // Les autres étapes à reprendre sont citées aussi : sinon seule l'erreur la plus grave apparaît
                string others = (_result.NotDone.Any() ? Lang.T("Not done: ", "Non faites : ") + StepNames.Display(_result.NotDone) + "\n" : "")
                              + (_result.AfterRestart.Any() ? Lang.T("To finish after restarting: ", "À terminer après le redémarrage : ") + StepNames.Display(_result.AfterRestart) + "\n" : "");
                ShowMessage(RunMessage, InfoBarSeverity.Error, Lang.T("Failed steps: ", "Étapes en échec : ") + StepNames.Display(_result.Failed),
                    others + Lang.T("Run the app again to retry them: the settings are kept and successful steps will not be redone.", "Relancez l'application pour les réessayer : la configuration est conservée et les étapes réussies ne seront pas refaites."));
            }
            else if (_result.NotDone.Any())
            {
                RunTitle.Text = Lang.T("Setup finished: some steps are not done", "Configuration terminée : des étapes ne sont pas faites");
                ShowMessage(RunMessage, InfoBarSeverity.Warning, Lang.T("Not done: ", "Non faites : ") + string.Join(", ", _result.NotDone.Select(s => _items[s].Title)),
                    string.Join("\n", _result.NotDone.Select(s => _items[s].Title + Lang.T(": ", " : ") + _items[s].Detail)) + "\n" +
                    Lang.T("Run the app again to retry them: the settings are kept.", "Relancez l'application pour les réessayer : la configuration est conservée."));
            }
            else if (_result.AfterRestart.Any())
            {
                RunTitle.Text = Lang.T("Restart needed", "Redémarrage nécessaire");
                ShowMessage(RunMessage, InfoBarSeverity.Warning, Lang.T("To finish after restarting: ", "À terminer après le redémarrage : ") + StepNames.Display(_result.AfterRestart),
                    Lang.T("After the restart, run the app again: the settings are kept and it will pick up where it stopped.",
                           "Après le redémarrage, relancez l’application : la configuration est conservée et elle reprendra là où elle s’est arrêtée."));
            }
            else if (warnings.Any())
            {
                RunTitle.Text = Lang.T("Setup finished", "Configuration terminée");
                ShowMessage(RunMessage, InfoBarSeverity.Warning, Lang.T("Please check", "À vérifier"), string.Join("\n", warnings.Select(w => w.Title + Lang.T(": ", " : ") + w.Detail)));
            }
            else
            {
                RunTitle.Text = Lang.T("Setup finished", "Configuration terminée");
                ShowMessage(RunMessage, InfoBarSeverity.Success, Lang.T("All done", "Tout est terminé"), Lang.T("The PC is ready.", "Le PC est prêt."));
            }
            RunSubtitle.Text = File.Exists(LogFile) ? Lang.T("Log: ", "Journal : ") + LogFile
                             : _result.LogCopyPath != null ? Lang.T("The working folder has been deleted. Copy of the log: ", "Le dossier de travail a été supprimé. Copie du journal : ") + _result.LogCopyPath
                             : Lang.T("The working folder has been deleted.", "Le dossier de travail a été supprimé.");
            OpenFolderButton.Visibility = Directory.Exists(_workDir) ? Visibility.Visible : Visibility.Collapsed;
            OpenReportButton.Visibility = _result.ReportPath != null ? Visibility.Visible : Visibility.Collapsed;
            RestartNowButton.Visibility = Visibility.Visible;
            Activate();

            // Arrêt demandé : pas de redémarrage automatique
            if (config.AutoRestart && !_result.StopRequested) StartRestartCountdown();
            else CloseButton.Visibility = Visibility.Visible;
        }

        private static string ShutdownComment => Lang.T("Robo's Ultimate Windows Configurator: restarting to finish the setup.",
                                                       "Robo's Ultimate Windows Configurator : redémarrage pour finaliser la configuration.");

        private void StartRestartCountdown()
        {
            // shutdown planifie le redémarrage côté Windows : il a lieu même si l'application est fermée
            if (!_options.Simulation) SystemInfo.Run(SystemInfo.SystemExe("shutdown.exe"), $"/r /t 60 /c \"{ShutdownComment}\"", timeout: TimeSpan.FromSeconds(30));
            _restartPending = true;
            CancelRestartButton.Visibility = Visibility.Visible;

            int secondsLeft = 60;
            RunSubtitle.Text = RestartText(secondsLeft);
            _restartTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _restartTimer.Tick += (s, e) =>
            {
                secondsLeft = Math.Max(0, secondsLeft - 1);
                RunSubtitle.Text = RestartText(secondsLeft);
                if (secondsLeft == 0) _restartTimer.Stop();
            };
            _restartTimer.Start();
        }

        private static string RestartText(int seconds) =>
            Lang.T($"The PC restarts in {seconds} seconds.", $"Le PC redémarre dans {seconds} secondes.");

        private void OnCancelRestartClick(object sender, RoutedEventArgs e)
        {
            if (!_options.Simulation) SystemInfo.Run(SystemInfo.SystemExe("shutdown.exe"), "/a", timeout: TimeSpan.FromSeconds(30));
            _restartPending = false;
            _restartTimer?.Stop();
            Log.Info(Lang.T("Restart cancelled", "Redémarrage annulé"));
            RunSubtitle.Text = Lang.T("Restart cancelled. Remember to restart the PC to finish the setup.", "Redémarrage annulé. Pensez à redémarrer le PC pour finaliser la configuration.");
            CancelRestartButton.Visibility = Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Visible;
        }

        private void OnRestartNowClick(object sender, RoutedEventArgs e)
        {
            if (_options.Simulation)
            {
                RunSubtitle.Text = Lang.T("Simulation mode: the PC is not restarted.", "Mode simulation : le PC n'est pas redémarré.");
                return;
            }
            if (_restartPending) SystemInfo.Run(SystemInfo.SystemExe("shutdown.exe"), "/a", timeout: TimeSpan.FromSeconds(30));
            SystemInfo.Run(SystemInfo.SystemExe("shutdown.exe"), $"/r /t 0 /c \"{ShutdownComment}\"", timeout: TimeSpan.FromSeconds(30));
        }

        private void OnOpenFolderClick(object sender, RoutedEventArgs e) => Process.Start(SystemInfo.WindowsExe("explorer.exe"), $"\"{_workDir}\"");

        private void OnOpenReportClick(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo(_result.ReportPath) { UseShellExecute = true });

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

        private void OnStopClick(object sender, RoutedEventArgs e) => RequestStop();

        private void RequestStop()
        {
            _engine?.RequestStop();
            StopButton.IsEnabled = false;
            ShowMessage(RunMessage, InfoBarSeverity.Informational, Lang.T("Stop requested", "Arrêt demandé"),
                Lang.T("The current step finishes, then the setup stops. Nothing is cleaned up and the PC does not restart.",
                       "L'étape en cours se termine, puis la configuration s'arrête. Rien n'est nettoyé et le PC ne redémarre pas."));
        }

        private async void OnClosing(object sender, CancelEventArgs e)
        {
            if (!_running) return;
            e.Cancel = true;
            var confirm = new Wpf.Ui.Controls.MessageBox
            {
                Title = Lang.T("Setup in progress", "Configuration en cours"),
                Content = Lang.T("“Stop after this step” lets the current step finish cleanly. “Quit now” interrupts it: it will be redone at the next launch.",
                                 "« Arrêter après cette étape » laisse l'étape en cours se terminer proprement. « Quitter maintenant » l'interrompt : elle sera refaite au prochain lancement."),
                PrimaryButtonText = Lang.T("Stop after this step", "Arrêter après cette étape"),
                SecondaryButtonText = Lang.T("Quit now", "Quitter maintenant"),
                CloseButtonText = Lang.T("Keep going", "Continuer"),
            };
            var choice = await confirm.ShowDialogAsync();
            if (choice == Wpf.Ui.Controls.MessageBoxResult.Primary) RequestStop();
            else if (choice == Wpf.Ui.Controls.MessageBoxResult.Secondary)
            {
                Log.Warn(Lang.T("Setup interrupted by the user", "Configuration interrompue par l'utilisateur"));
                Application.Current.Shutdown();
            }
        }
    }
}
