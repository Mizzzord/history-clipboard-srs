using System.Runtime.InteropServices;
using HistoryClipboard.Core;

namespace HistoryClipboard.Desktop.Platform;

internal sealed class MacClipboard : ISystemClipboard
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private readonly nint _pasteboard;
    private readonly nint _stringType;
    private readonly nint _fileType;
    private nint _activity;

    public MacClipboard()
    {
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        _pasteboard = Send(GetClass("NSPasteboard"), Selector("generalPasteboard"));
        _stringType = CreateString("public.utf8-plain-text");
        _fileType = CreateString("public.file-url");
        if (_pasteboard == 0 || _stringType == 0 || _fileType == 0)
            throw new ClipboardAccessException("Системный буфер недоступен.");
        Send(_pasteboard, Selector("retain"));
        Send(_stringType, Selector("retain"));
        Send(_fileType, Selector("retain"));
    }

    public long Version => SendLong(_pasteboard, Selector("changeCount"));

    public void SetMonitoring(bool active)
    {
        if (active == (_activity != 0))
            return;
        var pool = Send(GetClass("NSAutoreleasePool"), Selector("new"));
        try
        {
            var info = Send(GetClass("NSProcessInfo"), Selector("processInfo"));
            if (active)
            {
                _activity = SendActivity(info, Selector("beginActivityWithOptions:reason:"), 0x00EFFFFF,
                    CreateString("Сохранение истории буфера по запросу пользователя"));
                if (_activity == 0)
                    throw new ClipboardAccessException("Не удалось включить фоновый сбор.");
                Send(_activity, Selector("retain"));
            }
            else
            {
                SendArg(info, Selector("endActivity:"), _activity);
                Send(_activity, Selector("release"));
                _activity = 0;
            }
        }
        finally { Send(pool, Selector("drain")); }
    }

    public ClipboardSnapshot Read()
    {
        var pool = Send(GetClass("NSAutoreleasePool"), Selector("new"));
        try
        {
            var before = Version;
            var types = Send(_pasteboard, Selector("types"));
            var value = SendBoolArg(types, Selector("containsObject:"), _fileType) ? 0
                : SendArg(_pasteboard, Selector("stringForType:"), _stringType);
            var text = value == 0 ? null : Marshal.PtrToStringUTF8(Send(value, Selector("UTF8String")));
            var after = Version;
            if (before != after)
                throw new ClipboardAccessException("Буфер изменился во время чтения. Повторяем чтение.");
            return new ClipboardSnapshot(after, text);
        }
        finally
        {
            Send(pool, Selector("drain"));
        }
    }

    public ClipboardSnapshot Write(string text)
    {
        var pool = Send(GetClass("NSAutoreleasePool"), Selector("new"));
        try
        {
            var value = CreateString(text);
            SendLong(_pasteboard, Selector("clearContents"));
            if (!SendBoolArgs(_pasteboard, Selector("setString:forType:"), value, _stringType))
                throw new ClipboardAccessException("Не удалось записать текст в системный буфер.");
            return Read();
        }
        finally
        {
            Send(pool, Selector("drain"));
        }
    }

    public void Dispose()
    {
        SetMonitoring(false);
        Send(_pasteboard, Selector("release"));
        Send(_stringType, Selector("release"));
        Send(_fileType, Selector("release"));
    }

    private static nint CreateString(string value) => SendString(GetClass("NSString"), Selector("stringWithUTF8String:"), value);

    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern nint GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern nint Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBoolArg(nint receiver, nint selector, nint argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern long SendLong(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendArg(nint receiver, nint selector, nint argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendString(nint receiver, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendActivity(nint receiver, nint selector, ulong options, nint reason);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBoolArgs(nint receiver, nint selector, nint first, nint second);
}
