using ClipboardUsbMonitor.Commands;
using ClipboardUsbMonitor.Core;

namespace ClipboardUsbMonitor.Handlers.Windows;

sealed class WindowsControlHandler : IControlHandler
{
    public void Handle(PingCmd command) =>
        EventEmitter.Emit(new ReplyEvent(command.Id, true));

    public void Handle(ShutdownCmd command)
    {
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
        Environment.Exit(0);
    }
}
