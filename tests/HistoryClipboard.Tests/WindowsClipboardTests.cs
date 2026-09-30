using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HistoryClipboard.Core;
using HistoryClipboard.Desktop.Platform;
using Xunit;

namespace HistoryClipboard.Tests;

public sealed class WindowsNativeFactAttribute : FactAttribute
{
    public WindowsNativeFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("HISTORY_CLIPBOARD_NATIVE_TESTS") != "1")
            Skip = "Native clipboard check runs only on an explicitly enabled disposable Windows runner.";
    }
}

public sealed class WindowsClipboardTests
{
    [WindowsNativeFact]
    public void ExternalCopyNotificationsUnicodeOwnCopyAndFileFormats()
    {
        using var clipboard = new WindowsClipboard();
        var baseline = clipboard.Read();
        var policy = new CapturePolicy();
        policy.EstablishBaseline(baseline.Version);
        var text = "QA-Windows Привет 👋\r\n\tПолный текст  ";
        ExternalText(text);
        var snapshot = WaitForChange(clipboard, baseline.Version);
        Assert.Equal(text, snapshot.Text);
        Assert.Equal(CaptureResult.Accepted, policy.Observe(snapshot));
        ExternalText(text);
        var repeated = WaitForChange(clipboard, snapshot.Version);
        Assert.Equal(CaptureResult.Ignored, policy.Observe(repeated));
        ExternalText("QA-Windows Б");
        var second = WaitForChange(clipboard, repeated.Version);
        Assert.Equal(CaptureResult.Accepted, policy.Observe(second));
        ExternalText(text);
        var third = WaitForChange(clipboard, second.Version);
        Assert.Equal(CaptureResult.Accepted, policy.Observe(third));
        var own = clipboard.Write("QA-Windows Собственное копирование 👋");
        Assert.Equal("QA-Windows Собственное копирование 👋", own.Text);
        policy.SuppressOwnCopy(own);
        Assert.Equal(CaptureResult.Ignored, policy.Observe(clipboard.Read()));
        var file = Path.GetTempFileName();
        try
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(file));
            ExternalScript($"$d = New-Object System.Windows.Forms.DataObject; $files = New-Object System.Collections.Specialized.StringCollection; [void]$files.Add([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encoded}'))); $d.SetFileDropList($files); $d.SetText('QA-Windows File representation'); [System.Windows.Forms.Clipboard]::SetDataObject($d, $true)");
            Assert.Null(WaitForChange(clipboard, own.Version).Text);
        }
        finally
        {
            File.Delete(file);
            ExternalScript("[System.Windows.Forms.Clipboard]::Clear()");
        }
    }

    private static void ExternalText(string text)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        ExternalScript($"[System.Windows.Forms.Clipboard]::SetText([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encoded}')))");
    }

    private static void ExternalScript(string script)
    {
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-STA", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference = 'Stop'; Add-Type -AssemblyName System.Windows.Forms; " + script)) })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(15_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("External clipboard writer timed out.");
        }
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    private static ClipboardSnapshot WaitForChange(WindowsClipboard clipboard, long previous)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(2))
        {
            while (PeekMessage(out var message, 0, 0, 0, 1))
                DispatchMessage(ref message);
            if (clipboard.Version != previous)
                return clipboard.Read();
            Thread.Sleep(10);
        }
        throw new TimeoutException("Native clipboard notification was not delivered.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern nint DispatchMessage(ref Message message);
}
