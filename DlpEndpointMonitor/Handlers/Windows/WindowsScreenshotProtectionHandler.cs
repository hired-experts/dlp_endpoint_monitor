using DlpEndpointMonitor.Commands;
using DlpEndpointMonitor.Core;

namespace DlpEndpointMonitor.Handlers.Windows;

sealed class WindowsScreenshotProtectionHandler : IScreenshotProtectionHandler
{
    readonly ScreenshotBlockPolicy _policy;

    public WindowsScreenshotProtectionHandler(ScreenshotBlockPolicy policy)
    {
        _policy = policy;
    }

    public void Handle(ScreenshotBlockEnableCmd command)
    {
        _policy.SetEnabled(true);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(ScreenshotBlockDisableCmd command)
    {
        _policy.SetEnabled(false);
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
    }

    public void Handle(ScreenshotBlockStatusCmd command) =>
        EventEmitter.Emit(new ScreenshotBlockStatusEvent(command.Id, true, _policy.IsEnabled));
}
