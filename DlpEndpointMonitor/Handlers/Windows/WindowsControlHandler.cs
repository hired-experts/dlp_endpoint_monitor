using DlpEndpointMonitor.Commands;
using DlpEndpointMonitor.Core;

namespace DlpEndpointMonitor.Handlers.Windows;

sealed class WindowsControlHandler : IControlHandler
{
    readonly Action _stopCompanion;

    // stopCompanion: kills this primary's own --session-companion process before exiting, so an
    // explicit shutdown command doesn't leave it running as an orphan for the next start to find -
    // see SessionActions.TerminateCompanionProcesses. A no-op if no companion was launched.
    public WindowsControlHandler(Action stopCompanion)
    {
        _stopCompanion = stopCompanion;
    }

    public void Handle(PingCmd command) =>
        EventEmitter.Emit(new ReplyEvent(command.Id, true));

    public void Handle(ShutdownCmd command)
    {
        EventEmitter.Emit(new ReplyEvent(command.Id, true));
        _stopCompanion();
        Environment.Exit(0);
    }
}
