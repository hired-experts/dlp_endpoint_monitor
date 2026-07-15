using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DlpEndpointMonitor.Commands;
using DlpEndpointMonitor.Handlers;

namespace DlpEndpointMonitor.Core;

sealed class CommandDispatcher
{
    readonly CancellationToken            _cancellationToken;
    readonly IClipboardHandler            _clipboard;
    readonly IUsbStorageHandler           _usbStorage;
    readonly IUsbDeviceHandler            _usbDevice;
    readonly IUsbProtectionHandler        _usbProtection;
    readonly IClipboardProtectionHandler  _clipboardProtection;
    readonly IControlHandler              _control;
    readonly IAlertHandler                _alert;
    readonly IScreenshotProtectionHandler _screenshotProtection;

    public CommandDispatcher(
        CancellationToken            cancellationToken,
        IClipboardHandler            clipboard,
        IUsbStorageHandler           usbStorage,
        IUsbDeviceHandler            usbDevice,
        IUsbProtectionHandler        usbProtection,
        IClipboardProtectionHandler  clipboardProtection,
        IControlHandler              control,
        IAlertHandler                alert,
        IScreenshotProtectionHandler screenshotProtection)
    {
        _cancellationToken    = cancellationToken;
        _clipboard            = clipboard;
        _usbStorage           = usbStorage;
        _usbDevice            = usbDevice;
        _usbProtection        = usbProtection;
        _clipboardProtection  = clipboardProtection;
        _control              = control;
        _alert                = alert;
        _screenshotProtection = screenshotProtection;
    }

    public async Task RunAsync()
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            string? inputLine;
            try
            {
                inputLine = await Task.Run(Console.ReadLine, _cancellationToken);
            }
            catch (OperationCanceledException) { break; }

            if (inputLine is null)
            {
                EventEmitter.EmitInfo("stdin closed — monitoring continues");
                await Task.Delay(Timeout.Infinite, _cancellationToken).ContinueWith(_ => { });
                break;
            }

            Dispatch(inputLine);
        }
    }

    void Dispatch(string inputLine)
    {
        string? requestId  = null;
        string? rawCommand = null;
        try
        {
            using var document = JsonDocument.Parse(inputLine);
            var message        = document.RootElement;
            requestId          = message.GetProperty("id").GetString();
            rawCommand         = message.GetProperty("cmd").GetString();
            string rawJson     = message.GetRawText();

            CommandType? commandType = null;
            try { commandType = message.GetProperty("cmd").Deserialize(CommandsJsonContext.Default.CommandType); }
            catch (JsonException) { }

            if (commandType is null)
            {
                EventEmitter.Emit(new ReplyEvent(requestId, false, $"unknown command: {rawCommand}"));
                return;
            }

            switch (commandType.Value)
            {
                // ── Clipboard ─────────────────────────────────────────────────
                case CommandType.ClipboardRead:  _clipboard.Handle(Deserialize<ClipboardReadCmd>(rawJson));  break;
                case CommandType.ClipboardSet:   _clipboard.Handle(Deserialize<ClipboardSetCmd>(rawJson));   break;
                case CommandType.ClipboardClear: _clipboard.Handle(Deserialize<ClipboardClearCmd>(rawJson)); break;

                // ── Clipboard protection ──────────────────────────────────────
                case CommandType.ClipboardProtectionStatus: _clipboardProtection.Handle(Deserialize<ClipboardProtectionStatusCmd>(rawJson)); break;
                case CommandType.ClipboardWhitelistEnable:  _clipboardProtection.Handle(Deserialize<ClipboardWhitelistEnableCmd>(rawJson));  break;
                case CommandType.ClipboardWhitelistDisable: _clipboardProtection.Handle(Deserialize<ClipboardWhitelistDisableCmd>(rawJson)); break;
                case CommandType.ClipboardWhitelistGet:     _clipboardProtection.Handle(Deserialize<ClipboardWhitelistGetCmd>(rawJson));     break;
                case CommandType.ClipboardWhitelistClear:   _clipboardProtection.Handle(Deserialize<ClipboardWhitelistClearCmd>(rawJson));   break;
                case CommandType.ClipboardWhitelistAdd:     _clipboardProtection.Handle(Deserialize<ClipboardWhitelistAddCmd>(rawJson));     break;
                case CommandType.ClipboardWhitelistRemove:  _clipboardProtection.Handle(Deserialize<ClipboardWhitelistRemoveCmd>(rawJson));  break;
                case CommandType.ClipboardWhitelistSet:     _clipboardProtection.Handle(Deserialize<ClipboardWhitelistSetCmd>(rawJson));     break;
                case CommandType.ClipboardBlacklistEnable:  _clipboardProtection.Handle(Deserialize<ClipboardBlacklistEnableCmd>(rawJson));  break;
                case CommandType.ClipboardBlacklistDisable: _clipboardProtection.Handle(Deserialize<ClipboardBlacklistDisableCmd>(rawJson)); break;
                case CommandType.ClipboardBlacklistGet:     _clipboardProtection.Handle(Deserialize<ClipboardBlacklistGetCmd>(rawJson));     break;
                case CommandType.ClipboardBlacklistClear:   _clipboardProtection.Handle(Deserialize<ClipboardBlacklistClearCmd>(rawJson));   break;
                case CommandType.ClipboardBlacklistAdd:     _clipboardProtection.Handle(Deserialize<ClipboardBlacklistAddCmd>(rawJson));     break;
                case CommandType.ClipboardBlacklistRemove:  _clipboardProtection.Handle(Deserialize<ClipboardBlacklistRemoveCmd>(rawJson));  break;
                case CommandType.ClipboardBlacklistSet:     _clipboardProtection.Handle(Deserialize<ClipboardBlacklistSetCmd>(rawJson));     break;

                // ── USB — storage ─────────────────────────────────────────────
                case CommandType.UsbEject:          _usbStorage.Handle(Deserialize<UsbEjectCmd>(rawJson));          break;
                case CommandType.UsbDisableStorage: _usbStorage.Handle(Deserialize<UsbDisableStorageCmd>(rawJson)); break;
                case CommandType.UsbEnableStorage:  _usbStorage.Handle(Deserialize<UsbEnableStorageCmd>(rawJson));  break;
                case CommandType.UsbStorageStatus:  _usbStorage.Handle(Deserialize<UsbStorageStatusCmd>(rawJson));  break;

                // ── USB — device level ────────────────────────────────────────
                case CommandType.DeviceDisable: _usbDevice.Handle(Deserialize<DeviceDisableCmd>(rawJson)); break;
                case CommandType.DeviceEnable:  _usbDevice.Handle(Deserialize<DeviceEnableCmd>(rawJson));  break;

                // ── USB — protection ──────────────────────────────────────────
                case CommandType.DeviceProtectionStatus: _usbProtection.Handle(Deserialize<DeviceProtectionStatusCmd>(rawJson));  break;
                case CommandType.DeviceWhitelistEnable:  _usbProtection.Handle(Deserialize<DeviceWhitelistEnableCmd>(rawJson));   break;
                case CommandType.DeviceWhitelistDisable: _usbProtection.Handle(Deserialize<DeviceWhitelistDisableCmd>(rawJson));  break;
                case CommandType.DeviceWhitelistGet:     _usbProtection.Handle(Deserialize<DeviceWhitelistGetCmd>(rawJson));      break;
                case CommandType.DeviceWhitelistClear:   _usbProtection.Handle(Deserialize<DeviceWhitelistClearCmd>(rawJson));    break;
                case CommandType.DeviceWhitelistAdd:     _usbProtection.Handle(Deserialize<DeviceWhitelistAddCmd>(rawJson));      break;
                case CommandType.DeviceWhitelistRemove:  _usbProtection.Handle(Deserialize<DeviceWhitelistRemoveCmd>(rawJson));   break;
                case CommandType.DeviceWhitelistSet:     _usbProtection.Handle(Deserialize<DeviceWhitelistSetCmd>(rawJson));      break;
                case CommandType.DeviceBlacklistEnable:  _usbProtection.Handle(Deserialize<DeviceBlacklistEnableCmd>(rawJson));   break;
                case CommandType.DeviceBlacklistDisable: _usbProtection.Handle(Deserialize<DeviceBlacklistDisableCmd>(rawJson));  break;
                case CommandType.DeviceBlacklistGet:     _usbProtection.Handle(Deserialize<DeviceBlacklistGetCmd>(rawJson));      break;
                case CommandType.DeviceBlacklistClear:   _usbProtection.Handle(Deserialize<DeviceBlacklistClearCmd>(rawJson));    break;
                case CommandType.DeviceBlacklistAdd:     _usbProtection.Handle(Deserialize<DeviceBlacklistAddCmd>(rawJson));      break;
                case CommandType.DeviceBlacklistRemove:  _usbProtection.Handle(Deserialize<DeviceBlacklistRemoveCmd>(rawJson));   break;
                case CommandType.DeviceBlacklistSet:     _usbProtection.Handle(Deserialize<DeviceBlacklistSetCmd>(rawJson));      break;

                // ── Control ───────────────────────────────────────────────────
                case CommandType.Ping:           _control.Handle(Deserialize<PingCmd>(rawJson));           break;
                case CommandType.Shutdown:       _control.Handle(Deserialize<ShutdownCmd>(rawJson));       break;
                case CommandType.ResetAllPolicy: _control.Handle(Deserialize<ResetAllPolicyCmd>(rawJson)); break;
                case CommandType.SessionUserGet: _control.Handle(Deserialize<SessionUserGetCmd>(rawJson)); break;

                // ── Alert ─────────────────────────────────────────────────────
                case CommandType.ShowAlert: _alert.Handle(Deserialize<ShowAlertCmd>(rawJson)); break;

                // ── Screenshot ───────────────────────────────────────────────
                case CommandType.ScreenshotBlockEnable:  _screenshotProtection.Handle(Deserialize<ScreenshotBlockEnableCmd>(rawJson));  break;
                case CommandType.ScreenshotBlockDisable: _screenshotProtection.Handle(Deserialize<ScreenshotBlockDisableCmd>(rawJson)); break;
                case CommandType.ScreenshotBlockStatus:  _screenshotProtection.Handle(Deserialize<ScreenshotBlockStatusCmd>(rawJson));  break;
            }
        }
        catch (Exception ex)
        {
            EventEmitter.Emit(new ReplyEvent(requestId, false, ex.Message));
        }
    }

    static TCommand Deserialize<TCommand>(string rawJson)
    {
        var typeInfo = (JsonTypeInfo<TCommand>)CommandsJsonContext.Default.GetTypeInfo(typeof(TCommand))!;
        return JsonSerializer.Deserialize(rawJson, typeInfo)!;
    }
}
