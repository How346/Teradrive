using System.Collections;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TeraDrive.App.ViewModels;

namespace TeraDrive.App;

public partial class MainWindow : Window
{
    readonly DispatcherTimer _posTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    string? _currentKey; bool _playing, _seeking; int _saveTick;
    WindowState _prevState; WindowStyle _prevStyle; bool _full;

    public MainWindow()
    {
        InitializeComponent();
        _posTimer.Tick += PosTick;
        DataContextChanged += (_, _) => { if (DataContext is MainViewModel vm) vm.PlayRequested += OnPlayRequested; };
        Closing += (_, _) => SaveNow();
    }

    MainViewModel? Vm => DataContext as MainViewModel;

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { Fullscreen_Click(sender, e); e.Handled = true; return; }
        if (Vm is null || Vm.SelectedSection != 1 || Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;
        if (e.Key == Key.F2) { Vm.RenameCmd.Execute(FilesGrid.SelectedItems); e.Handled = true; }
        else if (e.Key == Key.Delete) { Vm.DeleteCmd.Execute(FilesGrid.SelectedItems); e.Handled = true; }
        else if (e.Key == Key.Back) { Vm.UpCmd.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Enter) { Vm.OpenRowCmd.Execute(FilesGrid.SelectedItems); e.Handled = true; }
    }

    void FilesGrid_DoubleClick(object sender, MouseButtonEventArgs e) => Vm?.OpenRowCmd.Execute(FilesGrid.SelectedItems);
    void VideoGrid_DoubleClick(object sender, MouseButtonEventArgs e) { if (VideoGrid.SelectedItem is VideoRow r) Vm?.PlayVideoCmd.Execute(r); }

    // ---- playback (WPF MediaElement; codec support depends on Windows) ----
    void OnPlayRequested(string path, string key, double start)
    {
        SaveNow();
        _currentKey = key; Player.Source = new Uri(path); _pendingStart = start; Player.Play(); _playing = true; _posTimer.Start();
    }
    double _pendingStart;

    void Player_Opened(object sender, RoutedEventArgs e)
    {
        if (!Player.NaturalDuration.HasTimeSpan) return;
        SeekSlider.Maximum = Player.NaturalDuration.TimeSpan.TotalSeconds;
        if (_pendingStart > 1 && _pendingStart < SeekSlider.Maximum - 5) Player.Position = TimeSpan.FromSeconds(_pendingStart); // resume
        _pendingStart = 0;
    }

    void Player_Failed(object? sender, ExceptionRoutedEventArgs e) { _posTimer.Stop(); _playing = false; Vm?.ReportVideoError(e.ErrorException.Message); }
    void Player_Ended(object sender, RoutedEventArgs e) { _playing = false; if (_currentKey is not null) Vm?.SavePosition(_currentKey, 0); }

    void PosTick(object? s, EventArgs e)
    {
        if (!Player.NaturalDuration.HasTimeSpan) return;
        _seeking = true; SeekSlider.Value = Player.Position.TotalSeconds; _seeking = false;
        TimeText.Text = $"{Player.Position:hh\\:mm\\:ss} / {Player.NaturalDuration.TimeSpan:hh\\:mm\\:ss}";
        if (++_saveTick % 10 == 0) SaveNow();
    }

    void SaveNow() { if (_currentKey is not null && Player.Source is not null) Vm?.SavePosition(_currentKey, Player.Position.TotalSeconds); }
    void PlayPause_Click(object s, RoutedEventArgs e) { if (Player.Source is null) return; if (_playing) { Player.Pause(); SaveNow(); } else Player.Play(); _playing = !_playing; }
    void Stop_Click(object s, RoutedEventArgs e) { SaveNow(); Player.Stop(); _playing = false; }
    void Volume_Changed(object s, RoutedPropertyChangedEventArgs<double> e) { if (Player is not null) Player.Volume = e.NewValue; }
    void SeekSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (!_seeking && Player.Source is not null) Player.Position = TimeSpan.FromSeconds(e.NewValue); }

    void Fullscreen_Click(object s, RoutedEventArgs e)
    {
        if (!_full) { _prevState = WindowState; _prevStyle = WindowStyle; WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; }
        else { WindowStyle = _prevStyle; WindowState = _prevState; }
        _full = !_full;
    }
}
