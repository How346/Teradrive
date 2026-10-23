using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace TeraDrive.App;

public static class Dialogs
{
    public static string[] PickFiles(string title) { var d = new OpenFileDialog { Title = title, Multiselect = true }; return d.ShowDialog() == true ? d.FileNames : Array.Empty<string>(); }
    public static string? PickExe() { var d = new OpenFileDialog { Title = "Select the game's executable", Filter = "Programs (*.exe;*.lnk;*.bat)|*.exe;*.lnk;*.bat|All files|*.*" }; return d.ShowDialog() == true ? d.FileName : null; }
    public static string? PickFolder(string title) { var d = new OpenFolderDialog { Title = title }; return d.ShowDialog() == true ? d.FolderName : null; }
    public static string? SaveFile(string title, string name) { var d = new SaveFileDialog { Title = title, FileName = name }; return d.ShowDialog() == true ? d.FileName : null; }
    public static bool Confirm(string message, string title = "TeraDrive") => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    public static void Info(string message) => MessageBox.Show(message, "TeraDrive", MessageBoxButton.OK, MessageBoxImage.Information);
    public static void Error(string message) => MessageBox.Show(message, "TeraDrive", MessageBoxButton.OK, MessageBoxImage.Warning);

    public static string? Prompt(string title, string label, string initial = "")
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 12), MinWidth = 320 };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = label }); panel.Children.Add(box); panel.Children.Add(buttons);
        var win = new Window { Title = title, Content = panel, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
        win.SetResourceReference(Window.BackgroundProperty, "BgBrush");
        ok.Click += (_, _) => win.DialogResult = true;
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true ? box.Text : null;
    }
}
