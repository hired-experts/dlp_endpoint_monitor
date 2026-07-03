using DlpEndpointMonitor.Actions;
using DlpEndpointMonitor.Core;

namespace DlpEndpointMonitor.Monitors;

sealed class ClipboardMonitor : IDisposable
{
    readonly MessageWindow _window;

    public ClipboardMonitor(MessageWindow window)
    {
        _window = window;
        _window.ClipboardChanged += OnClipboardChanged;
    }

    void OnClipboardChanged()
    {
        try
        {
            var content = ClipboardActions.Read();
            if (content is null) return;

            IEvent payload = content.Type switch
            {
                ClipboardContentType.Text => new ClipboardTextEvent(content.Operation, content.Text, EventEmitter.Ts()),
                ClipboardContentType.Files => new ClipboardFilesEvent(content.Operation, content.Files, EventEmitter.Ts()),
                ClipboardContentType.Image => new ClipboardImageEvent(EventEmitter.Ts()),
                _ => new ClipboardUnknownEvent(EventEmitter.Ts())
            };

            EventEmitter.Emit(payload);
        }
        catch (Exception ex)
        {
            EventEmitter.EmitError("clipboard_monitor", ex.Message);
        }
    }

    public void Dispose() =>
        _window.ClipboardChanged -= OnClipboardChanged;
}
