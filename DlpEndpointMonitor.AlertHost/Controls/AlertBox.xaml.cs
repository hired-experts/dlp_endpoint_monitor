using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DlpEndpointMonitor.AlertContracts;

namespace DlpEndpointMonitor.AlertHost.Controls;

public partial class AlertBox : UserControl
{
    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(
        nameof(Severity), typeof(AlertSeverity), typeof(AlertBox),
        new PropertyMetadata(AlertSeverity.Info, OnSeverityChanged));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(AlertBox),
        new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(AlertBox),
        new PropertyMetadata(string.Empty, OnMessageChanged));

    // Caller-supplied only (see AlertRequest.Id's doc comment) - never fabricated here. Null/empty
    // hides the whole ID row rather than showing an empty or made-up value.
    public static readonly DependencyProperty IdProperty = DependencyProperty.Register(
        nameof(Id), typeof(string), typeof(AlertBox),
        new PropertyMetadata(null, OnIdChanged));

    // Opt-in per window type (see the XAML comment on CloseButton) - collapsed/no-op unless a
    // window explicitly sets this, so Modal (Acknowledge button) and FullScreen (click-backdrop)
    // are unaffected by Toast's own X-button requirement.
    public static readonly DependencyProperty ShowCloseButtonProperty = DependencyProperty.Register(
        nameof(ShowCloseButton), typeof(bool), typeof(AlertBox),
        new PropertyMetadata(false, OnShowCloseButtonChanged));

    // ModalWindow places an Acknowledge button directly below AlertBox, inside the SAME outer
    // rounded card - AlertBox's own bottom corners (plus its own 1px BorderBrush stroke) would
    // otherwise draw a second, visibly rounded edge floating in the middle of that card, right
    // above the button. Square this control's own bottom corners off in that case and let the
    // OUTER container's rounding be the only rounded bottom edge; Toast/FullScreen (AlertBox used
    // standalone) keep the default of rounding all four corners themselves.
    public static readonly DependencyProperty RoundBottomCornersProperty = DependencyProperty.Register(
        nameof(RoundBottomCorners), typeof(bool), typeof(AlertBox),
        new PropertyMetadata(true, OnRoundBottomCornersChanged));

    public AlertSeverity Severity
    {
        get => (AlertSeverity)GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public string? Id
    {
        get => (string?)GetValue(IdProperty);
        set => SetValue(IdProperty, value);
    }

    public bool ShowCloseButton
    {
        get => (bool)GetValue(ShowCloseButtonProperty);
        set => SetValue(ShowCloseButtonProperty, value);
    }

    public bool RoundBottomCorners
    {
        get => (bool)GetValue(RoundBottomCornersProperty);
        set => SetValue(RoundBottomCornersProperty, value);
    }

    /// <summary>Raised when CloseButton is clicked - the owning Window decides what "close" means.</summary>
    public event EventHandler? CloseRequested;

    public AlertBox()
    {
        InitializeComponent();
        // Constructor runs before any property setter fires the *Changed callbacks below, so
        // apply the default severity's color up front rather than waiting on a first change.
        ApplySeverityColor(Severity);
    }

    static void OnSeverityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((AlertBox)d).ApplySeverityColor((AlertSeverity)e.NewValue);

    static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((AlertBox)d).TitleText.Text = (string)e.NewValue;

    static void OnMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        RichTextParser.Apply(((AlertBox)d).MessageText, (string)e.NewValue);

    static void OnIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (AlertBox)d;
        string? id = (string?)e.NewValue;
        box.IdRow.Visibility = string.IsNullOrEmpty(id) ? Visibility.Collapsed : Visibility.Visible;
        box.IdText.Text = string.IsNullOrEmpty(id) ? string.Empty : $"ID: {id}";
    }

    static void OnShowCloseButtonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((AlertBox)d).CloseButton.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;

    static void OnRoundBottomCornersChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((AlertBox)d).RootBorder.CornerRadius = (bool)e.NewValue
            ? new CornerRadius(20)
            : new CornerRadius(20, 20, 0, 0);

    void ApplySeverityColor(AlertSeverity severity)
    {
        HeaderBorder.Background = SeverityBrushes.Resolve(this, severity);
        TitleText.Foreground = (System.Windows.Media.Brush)FindResource("SeverityForegroundBrush");
    }

    // Stops an ordinary click on the card's own content from bubbling up to a parent window's
    // click-to-dismiss handler (Toast/FullScreen) - see the XAML comment on this Border for why.
    void OnBoxMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(Id))
            Clipboard.SetText(Id);
    }

    void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
