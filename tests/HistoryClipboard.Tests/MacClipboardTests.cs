using System.Diagnostics;
using System.Text;
using HistoryClipboard.Desktop.Platform;
using Xunit;

namespace HistoryClipboard.Tests;

public sealed class MacNativeFactAttribute : FactAttribute
{
    public MacNativeFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("HISTORY_CLIPBOARD_NATIVE_TESTS") != "1")
            Skip = "Native clipboard check runs only on an explicitly enabled disposable Mac runner.";
    }
}

public sealed class MacClipboardTests
{
    [MacNativeFact]
    public void ExternalCopyVersionUnicodeOwnCopyAndFileFormats()
    {
        using var clipboard = new MacClipboard();
        var baseline = clipboard.Read();
        var text = "QA-Mac Привет 👋\r\n\tПолный текст  ";
        using (var writer = Process.Start(new ProcessStartInfo("pbcopy") { RedirectStandardInput = true, StandardInputEncoding = new UTF8Encoding(false) })!)
        {
            writer.StandardInput.Write(text);
            writer.StandardInput.Close();
            Assert.True(writer.WaitForExit(10_000));
            Assert.Equal(0, writer.ExitCode);
        }
        var snapshot = clipboard.Read();
        Assert.NotEqual(baseline.Version, snapshot.Version);
        Assert.Equal(text, snapshot.Text);
        var own = clipboard.Write("QA-Mac Собственное копирование 👋");
        Assert.Equal("QA-Mac Собственное копирование 👋", own.Text);
        Assert.Equal(own, clipboard.Read());
        var file = Path.GetTempFileName();
        try
        {
            var start = new ProcessStartInfo("swift");
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("import AppKit; let p = NSPasteboard.general; p.clearContents(); guard p.writeObjects([NSURL(fileURLWithPath:CommandLine.arguments[1])]), p.setString(\"QA-Mac File representation\", forType:.string) else { fatalError(\"Cannot prepare clipboard fixture\") }");
            start.ArgumentList.Add(file);
            using var writer = Process.Start(start)!;
            if (!writer.WaitForExit(120_000))
            {
                writer.Kill(entireProcessTree: true);
                throw new TimeoutException("Swift clipboard fixture compilation or execution timed out.");
            }
            Assert.Equal(0, writer.ExitCode);
            Assert.Null(clipboard.Read().Text);
        }
        finally
        {
            File.Delete(file);
            clipboard.Write("");
        }
    }
}
