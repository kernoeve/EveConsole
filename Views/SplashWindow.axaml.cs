using Avalonia.Controls;
using Avalonia.Threading;
using System.Reflection;

namespace EveConsole.Views;

public partial class SplashWindow : Window
{
    private double _trackWidth;

    public SplashWindow()
    {
        InitializeComponent();

        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        SubVersionText.Text = ver is not null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "";
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _trackWidth = e.NewSize.Width - 96; // matches DockPanel Margin="48 0 48 28"
        UpdateFill();
    }

    public void ReportProgress(double percent, string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text = status;
            UpdateFill(percent);
        });
    }

    private void UpdateFill(double percent = -1)
    {
        if (percent >= 0)
            ProgressFill.Width = _trackWidth * Math.Clamp(percent, 0, 100) / 100.0;
    }
}
