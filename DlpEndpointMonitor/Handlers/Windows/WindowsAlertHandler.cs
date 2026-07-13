using DlpEndpointMonitor.Actions;
using DlpEndpointMonitor.AlertContracts;
using DlpEndpointMonitor.Commands;
using DlpEndpointMonitor.Core;

namespace DlpEndpointMonitor.Handlers.Windows;

sealed class WindowsAlertHandler : IAlertHandler
{
    public void Handle(ShowAlertCmd command)
    {
        var request = new AlertRequest(
            Type:     command.Type,
            Title:    command.Title,
            Message:  command.Message,
            Id:       command.SourceEventId,
            Severity: command.Severity,
            DurationSeconds: command.DurationSeconds ?? 5);

        var (ok, error) = AlertActions.ShowAlert(request);
        EventEmitter.Emit(new ReplyEvent(command.Id, ok, error));
    }
}
