using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
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

        private OemKey _oemKey;
        private bool _running;
        private bool _restartPending;
        private RunResult _result;
        private DispatcherTimer _restartTimer;

        public MainWindow(Options options)
        {
            _options = options;
            InitializeComponent();
            SystemThemeWatcher.Watch(this);

            _workDir = options.Simulation
                ? Path.Combine(Path.GetTempPath(), "RoboConfigurator-simulation")
                : Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "RoboConfigurator");
            Directory.CreateDirectory(_workDir);
            _configFile = Path.Combine(_workDir, "configuration.json");
            _stateFile = Path.Combine(_workDir, "etapes-terminees.txt");
            if (options.Reset) ForgetPreviousRun();
            _state = new StepState(_stateFile);
            Log.Open(Path.Combine(_workDir, "journal.txt"));
            Log.Written += OnLog;

            _steps = new List<StepItem>
            {
                new StepItem(StepNames.Ninite, "Ninite", "Installe 7-Zip, Chrome, Firefox, Foxit Reader et Notepad++. Cliquer sur « Close » à la fin.", SymbolRegular.AppsAddIn24),
                new StepItem(StepNames.CrapFixer, "CrapFixer", "Applique les tweaks recommandés et le thème sombre, supprime les applis inutiles (Candy Crush, Bing News...).", SymbolRegular.Broom24),
                new StepItem(StepNames.Edge, "Désinstaller Microsoft Edge", "Désinstalleur officiel d'Edge (méthode WinUtil). WebView2 est conservé.", SymbolRegular.Globe24),
                new StepItem(StepNames.Office, "Microsoft Office", "Installe Office et attend la fin de l'installation.", SymbolRegular.Document24),
                new StepItem(StepNames.Activation, "Activation de Windows", "Si Windows n'est pas activé : clé saisie, sinon clé OEM du BIOS, sinon script d'activation.", SymbolRegular.ShieldCheckmark24),
            };
            _runItems = _steps.Concat(new[]
            {
                new StepItem(Engine.RenameKey, "Nom du PC", "", SymbolRegular.Rename24),
                new StepItem(Engine.ReportKey, "Rapport du poste", "", SymbolRegular.DocumentBulletList24),
            }).ToList();
            _items = _runItems.ToDictionary(s => s.Key);
            foreach (var item in _runItems) item.PropertyChanged += OnStepChanged;
            foreach (var step in _steps) step.AlreadyDone = _state.IsDone(step.Key);
            StepsList.ItemsSource = _steps;
            RunList.ItemsSource = _runItems;

            PcNameText.Text = Environment.MachineName;
            NewNameBox.PlaceholderText = Environment.MachineName;
            if (options.Simulation) SimulationBadge.Visibility = Visibility.Visible;
            LoadConfig();

            Loaded += async (s, e) => await LoadSystemInfoAsync();
            Closing += OnClosing;
        }

        // -------------------------------------------------------------------
        // Configuration
        // -------------------------------------------------------------------
        private void LoadConfig()
        {
            RunConfig config = null;
            if (File.Exists(_configFile))
            {
                try
                {
                    config = RunConfig.Load(_configFile);
                    ShowMessage(ConfigMessage, InfoBarSeverity.Informational, "Reprise du lancement précédent",
                        "La configuration du lancement interrompu a été rechargée. Les étapes déjà faites seront passées.");
                }
                catch (Exception ex)
                {
                    ShowMessage(ConfigMessage, InfoBarSeverity.Warning, "Configuration précédente illisible", ex.Message);
                }
            }
            config = config ?? new RunConfig
            {
                Steps = StepNames.All.Except(_options.Skip).ToArray(),
                AllTweaks = _options.AllTweaks,
                WindowsKey = _options.WindowsKey,
            };
            if (_options.Errors.Any())
                ShowMessage(ConfigMessage, InfoBarSeverity.Error, "Options de lancement ignorées", string.Join("\n", _options.Errors));

            NewNameBox.Text = config.NewName;
            foreach (var step in _steps) step.IsSelected = config.Has(step.Key);
            TweaksBox.SelectedIndex = config.AllTweaks ? 1 : 0;
            KeyBox.Text = config.WindowsKey;
            RestartSwitch.IsChecked = config.AutoRestart;
        }

        private async Task LoadSystemInfoAsync()
        {
            string Try(Func<string> read) { try { return read(); } catch (Exception) { return ""; } }
            var info = await Task.Run(() => new
            {
                Model = Try(SystemInfo.Model),
                Windows = Try(SystemInfo.WindowsCaption),
                Activated = Try(() => SystemInfo.IsWindowsActivated() ? "oui" : "non"),
                Oem = SystemInfo.GetOemKey(),
            });

            PcModelText.Text = info.Model;
            WindowsText.Text = info.Windows;
            if (info.Activated == "oui") SetActivation(SymbolRegular.ShieldCheckmark24, "SystemFillColorSuccessBrush", "Windows est activé");
            else if (info.Activated == "non") SetActivation(SymbolRegular.ShieldError24, "SystemFillColorCautionBrush", "Windows n'est pas activé");
            else SetActivation(SymbolRegular.ShieldQuestion24, "TextFillColorSecondaryBrush", "Activation inconnue");

            _oemKey = info.Oem;
            if (_oemKey != null)
                KeyHint.Text = $"Clé du BIOS détectée ({_oemKey.Description}) : utilisée automatiquement si aucune clé n'est saisie.";
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
            ShowMessage(ConfigMessage, InfoBarSeverity.Success, "Tout sera refait",
                "Les étapes déjà faites et la configuration enregistrée ont été oubliées.");
        }

        // -------------------------------------------------------------------
        // Exécution
        // -------------------------------------------------------------------
        private async void OnStartClick(object sender, RoutedEventArgs e)
        {
            string name = NewNameBox.Text.Trim().ToUpperInvariant();
            if (name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) name = "";
            if (name.Length > 0 && !RunConfig.IsValidPcName(name))
            {
                ShowMessage(ConfigMessage, InfoBarSeverity.Error, "Nom du PC invalide",
                    "1 à 15 caractères : lettres, chiffres et tirets, pas uniquement des chiffres.");
                return;
            }
            string key = KeyBox.Text.Trim().ToUpperInvariant();
            if (key.Length > 0 && !RunConfig.IsValidKey(key))
            {
                ShowMessage(ConfigMessage, InfoBarSeverity.Error, "Clé Windows invalide", "Format attendu : XXXXX-XXXXX-XXXXX-XXXXX-XXXXX.");
                return;
            }

            var config = new RunConfig
            {
                NewName = name,
                Steps = _steps.Where(s => s.IsSelected).Select(s => s.Key).ToArray(),
                AllTweaks = TweaksBox.SelectedIndex == 1,
                WindowsKey = key,
                AutoRestart = RestartSwitch.IsChecked == true,
            };

            var confirm = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Démarrer la configuration ?",
                Content = config.AutoRestart
                    ? "Le PC va être configuré automatiquement, puis redémarrera à la fin."
                    : "Le PC va être configuré automatiquement jusqu'à la fin.",
                PrimaryButtonText = "Démarrer",
                CloseButtonText = "Annuler",
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

            var engine = new Engine(config, _workDir, _configFile, _state, _oemKey, _options.Simulation, _items);
            _running = true;
            try { _result = await Task.Run(engine.Run); }
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
                    ShowMessage(RunMessage, InfoBarSeverity.Informational, "Action requise",
                        "Quand Ninite affiche « Done », cliquez sur « Close » pour passer à la suite.");
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
            var warnings = _runItems.Where(s => s.Status == StepStatus.Warning).ToList();
            if (_result.Failed.Any())
            {
                RunTitle.Text = "Configuration terminée avec des erreurs";
                ShowMessage(RunMessage, InfoBarSeverity.Error, $"Étapes en échec : {string.Join(", ", _result.Failed)}",
                    "Relancez l'application pour les réessayer : la configuration est conservée et les étapes réussies ne seront pas refaites.");
            }
            else if (warnings.Any())
            {
                RunTitle.Text = "Configuration terminée";
                ShowMessage(RunMessage, InfoBarSeverity.Warning, "À vérifier", string.Join("\n", warnings.Select(w => $"{w.Title} : {w.Detail}")));
            }
            else
            {
                RunTitle.Text = "Configuration terminée";
                ShowMessage(RunMessage, InfoBarSeverity.Success, "Tout est terminé", "Le PC est prêt.");
            }
            RunSubtitle.Text = $"Journal : {Path.Combine(_workDir, "journal.txt")}";
            OpenReportButton.Visibility = _result.ReportPath != null ? Visibility.Visible : Visibility.Collapsed;
            RestartNowButton.Visibility = Visibility.Visible;
            Activate();

            if (config.AutoRestart) StartRestartCountdown();
            else CloseButton.Visibility = Visibility.Visible;
        }

        // -------------------------------------------------------------------
        // Redémarrage
        // -------------------------------------------------------------------
        private const string ShutdownComment = "Robo's Ultimate Windows Configurator : redémarrage pour finaliser la configuration.";

        private void StartRestartCountdown()
        {
            // shutdown planifie le redémarrage côté Windows : il a lieu même si l'application est fermée
            if (!_options.Simulation) SystemInfo.Run("shutdown.exe", $"/r /t 60 /c \"{ShutdownComment}\"");
            _restartPending = true;
            CancelRestartButton.Visibility = Visibility.Visible;

            int secondsLeft = 60;
            RunSubtitle.Text = $"Le PC redémarre dans {secondsLeft} secondes.";
            _restartTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _restartTimer.Tick += (s, e) =>
            {
                secondsLeft = Math.Max(0, secondsLeft - 1);
                RunSubtitle.Text = $"Le PC redémarre dans {secondsLeft} secondes.";
                if (secondsLeft == 0) _restartTimer.Stop();
            };
            _restartTimer.Start();
        }

        private void OnCancelRestartClick(object sender, RoutedEventArgs e)
        {
            if (!_options.Simulation) SystemInfo.Run("shutdown.exe", "/a");
            _restartPending = false;
            _restartTimer?.Stop();
            Log.Info("Redémarrage annulé");
            RunSubtitle.Text = "Redémarrage annulé. Pensez à redémarrer le PC pour finaliser la configuration.";
            CancelRestartButton.Visibility = Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Visible;
        }

        private void OnRestartNowClick(object sender, RoutedEventArgs e)
        {
            if (_options.Simulation)
            {
                RunSubtitle.Text = "Mode simulation : le PC n'est pas redémarré.";
                return;
            }
            if (_restartPending) SystemInfo.Run("shutdown.exe", "/a");
            SystemInfo.Run("shutdown.exe", $"/r /t 0 /c \"{ShutdownComment}\"");
        }

        // -------------------------------------------------------------------
        // Divers
        // -------------------------------------------------------------------
        private void OnOpenFolderClick(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", $"\"{_workDir}\"");

        private void OnOpenReportClick(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo(_result.ReportPath) { UseShellExecute = true });

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

        private async void OnClosing(object sender, CancelEventArgs e)
        {
            if (!_running) return;
            e.Cancel = true;
            var confirm = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Configuration en cours",
                Content = "Quitter maintenant interrompt la configuration. Au prochain lancement, l'application proposera de reprendre là où elle s'est arrêtée.",
                PrimaryButtonText = "Quitter",
                CloseButtonText = "Continuer",
            };
            if (await confirm.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary)
            {
                Log.Warn("Configuration interrompue par l'utilisateur");
                Application.Current.Shutdown();
            }
        }
    }
}
