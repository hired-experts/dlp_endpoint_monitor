using DlpEndpointMonitor.Actions;
using DlpEndpointMonitor.Commands;
using DlpEndpointMonitor.Core;

namespace DlpEndpointMonitor.Handlers.Windows;

sealed class WindowsUsbDeviceHandler : IUsbDeviceHandler
{
    public void Handle(DeviceDisableCmd command)
    {
        var (success, error) = UsbActions.DisableDevice(command.InstanceId);
        EventEmitter.Emit(new ReplyEvent(command.Id, success, error));
    }

    public void Handle(DeviceEnableCmd command)
    {
        var (success, error) = UsbActions.EnableDevice(command.InstanceId);
        EventEmitter.Emit(new ReplyEvent(command.Id, success, error));
    }
}
