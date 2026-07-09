using System.Windows;
using DlpEndpointMonitor.AlertContracts;
using DlpEndpointMonitor.AlertHost.Controls;

namespace DlpEndpointMonitor.AlertHost.Windows;

/// <summary>
/// The "user must acknowledge" alert type - centered, no auto-close timer. Dismissal is only via
/// the Acknowledge button (or an explicit close), which is what makes this distinct from the
/// transient Toast/FullScreen types.
/// </summary>
public partial class ModalWindow : Window
{
    readonly ModalScrimWindow _scrim;

    public ModalWindow(AlertRequest request)
    {
        InitializeComponent();
        Box.Severity = request.Severity;
        Box.Title = request.Title;
        Box.Message = request.Message;
        Box.Id = request.Id;
        AcknowledgeButton.Background = SeverityBrushes.Resolve(Box, request.Severity);

        // Shown before this window so Modal (shown afterward by the caller via ShowDialog) stacks
        // on top of it - see ModalScrimWindow's own doc comment for why Modal specifically gets
        // this dimmed backdrop instead of just the drop shadow Toast/FullScreen rely on.
        _scrim = new ModalScrimWindow();
        _scrim.Show();
        Closed += (_, _) => _scrim.Close();
    }

    void OnAcknowledgeClick(object sender, RoutedEventArgs e) => Close();
}
