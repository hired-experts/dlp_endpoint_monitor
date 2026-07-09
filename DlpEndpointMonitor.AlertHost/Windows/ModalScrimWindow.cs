using System.Windows;
using System.Windows.Media;

namespace DlpEndpointMonitor.AlertHost.Windows;

/// <summary>
/// A full-screen, semi-transparent dark overlay shown behind ModalWindow only. Modal is the
/// "must acknowledge" alert type - unlike Toast/FullScreen, which can rely on a drop shadow
/// alone, Modal gets the strongest treatment: darkening everything else guarantees the white
/// card is visible regardless of what's behind it (a white browser tab, another white app),
/// and it doubles as real modality - clicks land on this window, not whatever was in focus,
/// so the user cannot ignore the alert by clicking through to the app behind it. Purely visual;
/// it has no button, no click handler, and is never itself the thing that dismisses Modal (only
/// the Acknowledge button does that) - clicking it should not close anything, since that would
/// contradict the "must acknowledge" intent that makes Modal different from Toast/FullScreen's
/// click-to-dismiss backdrops. No XAML needed - there is nothing here but a colored rectangle.
/// </summary>
sealed class ModalScrimWindow : Window
{
    public ModalScrimWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0));
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
    }
}
