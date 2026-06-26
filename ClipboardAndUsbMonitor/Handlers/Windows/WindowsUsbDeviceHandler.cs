using ClipboardUsbMonitor.Actions;
using ClipboardUsbMonitor.Commands;
using ClipboardUsbMonitor.Core;

namespace ClipboardUsbMonitor.Handlers.Windows;

sealed class WindowsUsbDeviceHandler : IUsbDeviceHandler
{
    public void Handle(UsbDeviceDisableCmd command)
    {
        var (success, error) = UsbActions.DisableDevice(command.InstanceId);
        EventEmitter.Emit(new ReplyEvent(command.Id, success, error));
    }

    public void Handle(UsbDeviceEnableCmd command)
    {
        var (success, error) = UsbActions.EnableDevice(command.InstanceId);
        EventEmitter.Emit(new ReplyEvent(command.Id, success, error));
    }
}
