using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ClipboardUsbMonitor.Commands;
using ClipboardUsbMonitor.Handlers;

namespace ClipboardUsbMonitor.Core;

sealed class CommandDispatcher
{
    readonly CancellationToken     _cancellationToken;
    readonly IClipboardHandler     _clipboard;
    readonly IUsbStorageHandler    _usbStorage;
    readonly IUsbDeviceHandler     _usbDevice;
    readonly IUsbProtectionHandler _usbProtection;
    readonly IControlHandler       _control;

    public CommandDispatcher(
        CancellationToken     cancellationToken,
        IClipboardHandler     clipboard,
        IUsbStorageHandler    usbStorage,
        IUsbDeviceHandler     usbDevice,
        IUsbProtectionHandler usbProtection,
        IControlHandler       control)
    {
        _cancellationToken = cancellationToken;
        _clipboard         = clipboard;
        _usbStorage        = usbStorage;
        _usbDevice         = usbDevice;
        _usbProtection     = usbProtection;
        _control           = control;
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

                // ── USB — storage ─────────────────────────────────────────────
                case CommandType.UsbEject:          _usbStorage.Handle(Deserialize<UsbEjectCmd>(rawJson));          break;
                case CommandType.UsbDisableStorage: _usbStorage.Handle(Deserialize<UsbDisableStorageCmd>(rawJson)); break;
                case CommandType.UsbEnableStorage:  _usbStorage.Handle(Deserialize<UsbEnableStorageCmd>(rawJson));  break;
                case CommandType.UsbStorageStatus:  _usbStorage.Handle(Deserialize<UsbStorageStatusCmd>(rawJson));  break;

                // ── USB — device level ────────────────────────────────────────
                case CommandType.UsbDeviceDisable: _usbDevice.Handle(Deserialize<UsbDeviceDisableCmd>(rawJson)); break;
                case CommandType.UsbDeviceEnable:  _usbDevice.Handle(Deserialize<UsbDeviceEnableCmd>(rawJson));  break;

                // ── USB — protection ──────────────────────────────────────────
                case CommandType.UsbProtectionStatus: _usbProtection.Handle(Deserialize<UsbProtectionStatusCmd>(rawJson));  break;
                case CommandType.UsbWhitelistEnable:  _usbProtection.Handle(Deserialize<UsbWhitelistEnableCmd>(rawJson));   break;
                case CommandType.UsbWhitelistDisable: _usbProtection.Handle(Deserialize<UsbWhitelistDisableCmd>(rawJson));  break;
                case CommandType.UsbWhitelistGet:     _usbProtection.Handle(Deserialize<UsbWhitelistGetCmd>(rawJson));      break;
                case CommandType.UsbWhitelistClear:   _usbProtection.Handle(Deserialize<UsbWhitelistClearCmd>(rawJson));    break;
                case CommandType.UsbWhitelistAdd:     _usbProtection.Handle(Deserialize<UsbWhitelistAddCmd>(rawJson));      break;
                case CommandType.UsbWhitelistRemove:  _usbProtection.Handle(Deserialize<UsbWhitelistRemoveCmd>(rawJson));   break;
                case CommandType.UsbWhitelistSet:     _usbProtection.Handle(Deserialize<UsbWhitelistSetCmd>(rawJson));      break;
                case CommandType.UsbBlacklistEnable:  _usbProtection.Handle(Deserialize<UsbBlacklistEnableCmd>(rawJson));   break;
                case CommandType.UsbBlacklistDisable: _usbProtection.Handle(Deserialize<UsbBlacklistDisableCmd>(rawJson));  break;
                case CommandType.UsbBlacklistGet:     _usbProtection.Handle(Deserialize<UsbBlacklistGetCmd>(rawJson));      break;
                case CommandType.UsbBlacklistClear:   _usbProtection.Handle(Deserialize<UsbBlacklistClearCmd>(rawJson));    break;
                case CommandType.UsbBlacklistAdd:     _usbProtection.Handle(Deserialize<UsbBlacklistAddCmd>(rawJson));      break;
                case CommandType.UsbBlacklistRemove:  _usbProtection.Handle(Deserialize<UsbBlacklistRemoveCmd>(rawJson));   break;
                case CommandType.UsbBlacklistSet:     _usbProtection.Handle(Deserialize<UsbBlacklistSetCmd>(rawJson));      break;

                // ── Control ───────────────────────────────────────────────────
                case CommandType.Ping:     _control.Handle(Deserialize<PingCmd>(rawJson));     break;
                case CommandType.Shutdown: _control.Handle(Deserialize<ShutdownCmd>(rawJson)); break;
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
