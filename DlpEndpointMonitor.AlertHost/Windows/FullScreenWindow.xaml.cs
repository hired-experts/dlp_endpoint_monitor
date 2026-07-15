using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DlpEndpointMonitor.AlertContracts;

namespace DlpEndpointMonitor.AlertHost.Windows;

/// <summary>
/// Full-screen severity-color backdrop with the shared AlertBox centered on top. Auto-closes
/// after Request.DurationSeconds or on a click anywhere on the backdrop, whichever comes first.
/// </summary>
public partial class FullScreenWindow : Window
{
    readonly DispatcherTimer _timer;

    public FullScreenWindow(AlertRequest request, Rect screenBounds)
    {
        InitializeComponent();

        // Caller-supplied per-monitor bounds (MonitorHelper), not WindowState=Maximized - see the
        // XAML comment for why Maximized alone does not actually reach the screen edges here.
        Left = screenBounds.Left;
        Top = screenBounds.Top;
        Width = screenBounds.Width;
        Height = screenBounds.Height;

        Box.Severity = request.Severity;
        Box.Title = request.Title;
        Box.Message = request.Message;
        Box.Id = request.Id;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(1, request.DurationSeconds)),
        };
        _timer.Tick += (_, _) => Close();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    void OnClicked(object sender, MouseButtonEventArgs e) => Close();
}
