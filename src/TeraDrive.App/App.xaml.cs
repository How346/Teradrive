using System.Windows;
using TeraDrive.App.ViewModels;

namespace TeraDrive.App;

public partial class App : Application
{
    MainViewModel? _vm;

    public static void ApplyTheme(string theme)
    {
        var dict = new ResourceDictionary { Source = new Uri($"Themes/{(theme == "Light" ? "Light" : "Dark")}.xaml", UriKind.Relative) };
        Current.Resources.MergedDictionaries[0] = dict;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            _vm?.Log.Error("Unhandled UI exception: " + args.Exception);
            MessageBox.Show("Something went wrong: " + TeraDrive.Core.ErrorTranslator.Describe(args.Exception), "TeraDrive", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        try
        {
            _vm = new MainViewModel();
            ApplyTheme(_vm.Settings.Theme);
            var w = new MainWindow { DataContext = _vm };
            w.Show();
            await _vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show("TeraDrive could not start: " + TeraDrive.Core.ErrorTranslator.Describe(ex), "TeraDrive", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _vm?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(6)); } catch (Exception) { }
        base.OnExit(e);
    }
}
