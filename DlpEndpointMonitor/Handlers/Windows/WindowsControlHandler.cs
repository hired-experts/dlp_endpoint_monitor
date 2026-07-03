using DlpEndpointMonitor.Commands;
using DlpEndpointMonitor.Core;

namespace DlpEndpointMonitor.Handlers.Windows;

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
