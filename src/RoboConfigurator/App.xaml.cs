using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using RoboConfigurator.Core;
using Wpf.Ui.Appearance;

namespace RoboConfigurator
{
    public partial class App : Application
    {
        // Gardé en champ : sans référence, le ramasse-miettes pourrait libérer le mutex pendant la configuration
        private static Mutex _singleInstance;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += OnUnhandledException;
            // Erreur sur un autre thread que l'interface : l'application va s'arrêter, la cause reste au moins dans le journal
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
                Log.Error(Lang.T($"Fatal error: {args.ExceptionObject}", $"Erreur fatale : {args.ExceptionObject}"));
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                Log.Error(Lang.T($"Unexpected error in a background task: {args.Exception}", $"Erreur inattendue dans une tâche de fond : {args.Exception}"));
                args.SetObserved();
            };

            var options = Options.Parse(e.Args);
            // Deux configurations en même temps écriraient dans le même dossier, le même journal et la même liste d'étapes faites
            _singleInstance = new Mutex(true, options.Simulation ? @"Global\RoboConfigurator-Simulation" : @"Global\RoboConfigurator", out bool first);
            if (!first)
            {
                ShowExistingWindow();
                System.Windows.MessageBox.Show(Lang.T("Robo's Ultimate Windows Configurator is already open.", "Robo's Ultimate Windows Configurator est déjà ouvert."),
                    "Robo's Ultimate Windows Configurator", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            ApplicationThemeManager.ApplySystemTheme(true);
            MainWindow = new MainWindow(options);
            MainWindow.Show();
        }

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private static void ShowExistingWindow()
        {
            using (var current = Process.GetCurrentProcess())
            {
                var other = Process.GetProcessesByName(current.ProcessName).FirstOrDefault(p => p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero);
                if (other == null) return;
                ShowWindow(other.MainWindowHandle, 9);   // SW_RESTORE
                SetForegroundWindow(other.MainWindowHandle);
            }
        }

        private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error(Lang.T($"Unexpected error: {e.Exception}", $"Erreur inattendue : {e.Exception}"));
            System.Windows.MessageBox.Show(e.Exception.Message, "Robo's Ultimate Windows Configurator",
                MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
