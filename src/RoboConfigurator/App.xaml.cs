using System.Windows;
using System.Windows.Threading;
using RoboConfigurator.Core;
using Wpf.Ui.Appearance;

namespace RoboConfigurator
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += OnUnhandledException;
            ApplicationThemeManager.ApplySystemTheme(true);

            MainWindow = new MainWindow(Options.Parse(e.Args));
            MainWindow.Show();
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
