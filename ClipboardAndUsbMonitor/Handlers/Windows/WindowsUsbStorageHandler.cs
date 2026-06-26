using ClipboardUsbMonitor.Actions;
using ClipboardUsbMonitor.Commands;
using ClipboardUsbMonitor.Core;

namespace ClipboardUsbMonitor.Handlers.Windows;

sealed class WindowsUsbStorageHandler : IUsbStorageHandler
{
    public void Handle(UsbEjectCmd command)
    {
        var (success, error) = UsbActions.EjectDrive(command.Drive);
        EventEmitter.Emit(new ReplyEvent(command.Id, success, error));
    }

    public void Handle(UsbDisableStorageCmd command)
    {
        var (success, error) = UsbActions.SetUsbStorageEnabled(false);
        EventEmitter.Emit(new ReplyEvent(command.Id, success, error));
    }

    public void Handle(UsbEnableStorageCmd command)
    {
        var (success, error) = UsbActions.SetUsbStorageEnabled(true);
        EventEmitter.Emit(new ReplyEvent(command.Id, success, error));
    }

    public void Handle(UsbStorageStatusCmd command) =>
        EventEmitter.Emit(new UsbStorageStatusEvent(command.Id, true, UsbActions.IsUsbStorageEnabled()));
}
