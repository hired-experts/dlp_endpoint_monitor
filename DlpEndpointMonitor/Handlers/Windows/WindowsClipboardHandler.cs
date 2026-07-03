using DlpEndpointMonitor.Actions;
using DlpEndpointMonitor.Commands;
using DlpEndpointMonitor.Core;

namespace DlpEndpointMonitor.Handlers.Windows;

sealed class WindowsClipboardHandler : IClipboardHandler
{
    public void Handle(ClipboardReadCmd command) =>
        EventEmitter.Emit(new ClipboardReadEvent(command.Id, true, ClipboardActions.Read()));

    public void Handle(ClipboardSetCmd command)
    {
        bool success = ClipboardActions.SetText(command.Content);
        EventEmitter.Emit(new ReplyEvent(command.Id, success, success ? null : "SetClipboardData failed"));
    }

    public void Handle(ClipboardClearCmd command) =>
        EventEmitter.Emit(new ReplyEvent(command.Id, ClipboardActions.Clear()));
}
