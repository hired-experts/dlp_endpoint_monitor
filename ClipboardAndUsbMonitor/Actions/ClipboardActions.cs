using System.Runtime.InteropServices;
using System.Text;
using ClipboardUsbMonitor.Win32;


namespace ClipboardUsbMonitor.Actions;

public enum ClipboardContentType {
    Text,
    Files,
    Image,
    Unknown
}

public record ClipboardContent(
    ClipboardContentType Type,
    string? Text,
    string[]? Files,
    string Operation
);

static class ClipboardActions
{
    static readonly uint CF_DROP_EFFECT = NativeMethods.RegisterClipboardFormat("Preferred DropEffect");

    const uint DROPEFFECT_MOVE = 2;

    public static ClipboardContent? Read()
    {
        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
        {
            return null;
        }

        try
        {
            return TryReadText()
                ?? TryReadFiles()
                ?? TryReadUnknown();
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public static bool SetText(string text)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
        IntPtr hMem  = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (hMem == IntPtr.Zero)
        {
            return false;
        }

        IntPtr ptr = NativeMethods.GlobalLock(hMem);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        NativeMethods.GlobalUnlock(hMem);

        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
        {
            return false;
        }

        try
        {
            NativeMethods.EmptyClipboard();
            // Windows takes ownership of hMem after SetClipboardData — do not free it
            return NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, hMem) != IntPtr.Zero;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public static bool Clear()
    {
        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
        {
            return false;
        }

        try {
            return NativeMethods.EmptyClipboard();
        }
        finally {
            NativeMethods.CloseClipboard();
        }
    }

    static ClipboardContent? TryReadText()
    {
        if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_UNICODETEXT))
        {
            return null;
        }

        IntPtr hData = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
        if (hData == IntPtr.Zero)
        {
            return null;
        }

        IntPtr ptr = NativeMethods.GlobalLock(hData);
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            string? text = Marshal.PtrToStringUni(ptr);
            return text is null ? null
                : new ClipboardContent(ClipboardContentType.Text, text, null, "copy");
        }
        finally {
            NativeMethods.GlobalUnlock(hData);
        }
    }

    static ClipboardContent? TryReadFiles()
    {
        if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_HDROP))
        {
            return null;
        }

        IntPtr hDrop = NativeMethods.GetClipboardData(NativeMethods.CF_HDROP);
        if (hDrop == IntPtr.Zero)
        {
            return null;
        }

        uint count = NativeMethods.DragQueryFile(hDrop, NativeMethods.DRAGQUERY_COUNT, null, 0);
        if (count == 0)
        {
            return null;
        }

        var paths = new string[count];
        for (uint i = 0; i < count; i++)
        {
            // Ask Windows for the exact length first — no hardcoded MAX_PATH
            uint len = NativeMethods.DragQueryFile(hDrop, i, null, 0);
            if (len == 0)
            {
                continue;
            }

            var sb = new StringBuilder((int)len + 1); // +1 for null terminator
            NativeMethods.DragQueryFile(hDrop, i, sb, (uint)sb.Capacity);
            paths[i] = sb.ToString();
        }

        string op = ReadDropEffect();
        return new ClipboardContent(ClipboardContentType.Files, null, paths, op);
    }

    static ClipboardContent TryReadUnknown()
    {
        var formats = new List<uint>();
        uint fmt = 0;

        while ((fmt = NativeMethods.EnumClipboardFormats(fmt)) != 0)
        {
            formats.Add(fmt);
        }

        var type = formats.Contains(NativeMethods.CF_DIB)
            ? ClipboardContentType.Image
            : ClipboardContentType.Unknown;

        return new ClipboardContent(type, null, null, "copy");
    }

    static string ReadDropEffect()
    {
        if (CF_DROP_EFFECT == 0
            || !NativeMethods.IsClipboardFormatAvailable(CF_DROP_EFFECT))
        {
            return "copy";
        }

        IntPtr hData = NativeMethods.GetClipboardData(CF_DROP_EFFECT);
        if (hData == IntPtr.Zero)
        {
            return "copy";
        }

        IntPtr ptr = NativeMethods.GlobalLock(hData);
        if (ptr == IntPtr.Zero)
        {
            return "copy";
        }

        try
        {
            uint effect = (uint)Marshal.ReadInt32(ptr);
            return effect == DROPEFFECT_MOVE ? "cut" : "copy";
        }
        finally {
            NativeMethods.GlobalUnlock(hData);
        }
    }
}
