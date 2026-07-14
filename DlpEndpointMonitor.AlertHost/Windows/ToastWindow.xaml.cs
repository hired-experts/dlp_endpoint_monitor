using System.Windows;
using System.Windows.Threading;
using DlpEndpointMonitor.AlertContracts;

namespace DlpEndpointMonitor.AlertHost.Windows;

/// <summary>
/// Transient bottom-right corner toast. Auto-closes after Request.DurationSeconds, or on the X
/// button in its header - NOT on a click anywhere on the toast (that made selecting/copying the
/// message, or clicking the ID's Copy button, dismiss it by accident).
/// </summary>
public partial class ToastWindow : Window
{
    readonly DispatcherTimer _timer;

    public ToastWindow(AlertRequest request, Rect workArea)
    {
        InitializeComponent();
        Box.Severity = request.Severity;
        Box.Title = request.Title;
        Box.Message = request.Message;
        Box.Id = request.Id;
        Box.ShowCloseButton = true;
        Box.CloseRequested += (_, _) => Close();

        Loaded += (_, _) => PositionBottomRight(workArea);

        _timer = new DispatcherTimer
        {
            // A non-positive duration would never fire; floor it so a bad/zero value still
            // dismisses rather than hanging the alert queue's dispatch loop indefinitely.
            Interval = TimeSpan.FromSeconds(Math.Max(1, request.DurationSeconds)),
        };
        _timer.Tick += (_, _) => Close();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    void PositionBottomRight(Rect workArea)
    {
        const double margin = 24;
        Left = workArea.Right - ActualWidth - margin;
        Top = workArea.Bottom - ActualHeight - margin;
    }
}
