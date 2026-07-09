using System.Windows;
using System.Windows.Media;
using DlpEndpointMonitor.AlertContracts;

namespace DlpEndpointMonitor.AlertHost.Controls;

/// <summary>
/// The one place that maps an AlertSeverity to the resource key of its brand-color brush
/// (Resources/Colors.xaml). AlertBox's header band and FullScreenWindow's solid backdrop must
/// render the exact same severity color - routing both through this helper is what keeps that
/// true instead of two hand-typed switch statements silently drifting apart.
/// </summary>
static class SeverityBrushes
{
    public static Brush Resolve(FrameworkElement scope, AlertSeverity severity)
    {
        string key = severity switch
        {
            AlertSeverity.Warning => "WarningBrush",
            AlertSeverity.Blocked => "BlockedBrush",
            // Info, and any future/unmapped enum value, fails safe to the least alarming color
            // rather than throwing on a lookup miss.
            _ => "InfoBrush",
        };
        return (Brush)scope.FindResource(key);
    }
}
