using DlpEndpointMonitor.Actions;
using DlpEndpointMonitor.Commands;
using DlpEndpointMonitor.Core;

namespace DlpEndpointMonitor.Handlers.Windows;

sealed class WindowsUsbStorageHandler : IUsbStorageHandler
{
    readonly Action _blockAlreadyConnectedStorage;
    readonly Action _restoreStorageDisabled;

    // Delegate-injected (mirrors WindowsUsbProtectionHandler's applyPolicy/restoreDevices) rather
    // than holding a direct UsbMonitor reference - see Program.cs wiring.
    public WindowsUsbStorageHandler(Action blockAlreadyConnectedStorage, Action restoreStorageDisabled)
    {
        _blockAlreadyConnectedStorage = blockAlreadyConnectedStorage;
        _restoreStorageDisabled       = restoreStorageDisabled;
    }

    public void Handle(UsbEjectCmd command)
    {
        var (success, error) = UsbActions.EjectDrive(command.Drive);
        EventEmitter.Emit(new ReplyEvent(command.Id, success, error));
    }

    public void Handle(UsbDisableStorageCmd command)
    {
        var (success, error) = UsbActions.SetUsbStorageEnabled(false);
        // Only sweep on a successful registry write - a failed write means the kill switch
        // itself never took effect, so there is nothing new to retroactively enforce.
        if (success) Task.Run(_blockAlreadyConnectedStorage);
        EventEmitter.Emit(new ReplyEvent(command.Id, success, error));
    }

    public void Handle(UsbEnableStorageCmd command)
    {
        var (success, error) = UsbActions.SetUsbStorageEnabled(true);
        if (success) Task.Run(_restoreStorageDisabled);
        EventEmitter.Emit(new ReplyEvent(command.Id, success, error));
    }

    public void Handle(UsbStorageStatusCmd command) =>
        EventEmitter.Emit(new UsbStorageStatusEvent(command.Id, true, UsbActions.IsUsbStorageEnabled()));
}
