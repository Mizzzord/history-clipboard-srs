using HistoryClipboard.Core;
using Xunit;

namespace HistoryClipboard.Tests;

public sealed class CapturePolicyTests
{
    [Fact]
    public void StartupDoesNotCaptureExistingClipboardButNewCopyDoes()
    {
        var policy = new CapturePolicy();
        policy.EstablishBaseline(10);
        Assert.Equal(CaptureResult.Ignored, policy.Observe(new(10, "старый текст")));
        Assert.Equal(CaptureResult.Accepted, policy.Observe(new(11, "старый текст")));
    }

    [Fact]
    public void OnlyConsecutiveIdenticalTextsAreSkipped()
    {
        var policy = new CapturePolicy();
        Assert.Equal(CaptureResult.Accepted, policy.Observe(new(1, "А")));
        Assert.Equal(CaptureResult.Ignored, policy.Observe(new(2, "А")));
        Assert.Equal(CaptureResult.Accepted, policy.Observe(new(3, "Б")));
        Assert.Equal(CaptureResult.Accepted, policy.Observe(new(4, "А")));
    }

    [Theory]
    [InlineData(null, CaptureResult.Ignored)]
    [InlineData("", CaptureResult.Ignored)]
    [InlineData(" \t\r\n ", CaptureResult.Accepted)]
    public void EmptyAndWhitespaceAreDistinguished(string? text, CaptureResult expected)
    {
        Assert.Equal(expected, new CapturePolicy().Observe(new(1, text)));
    }

    [Fact]
    public void LimitCountsUtf8BytesAndDoesNotTruncate()
    {
        var policy = new CapturePolicy();
        var exact = new string('я', CapturePolicy.MaxBytes / 2);
        Assert.Equal(CaptureResult.Accepted, policy.Observe(new(1, exact)));
        Assert.Equal(CaptureResult.TooLarge, policy.Observe(new(2, exact + "a")));
        Assert.Equal(CaptureResult.Ignored, policy.Observe(new(2, exact + "a")));
        Assert.Equal(CaptureResult.Ignored, policy.Observe(new(3, exact + "a")));
    }

    [Fact]
    public void OwnCopyIsSkippedWithoutBlockingLaterExternalDifferentText()
    {
        var policy = new CapturePolicy();
        policy.Observe(new(1, "А"));
        policy.Observe(new(2, "Б"));
        policy.SuppressOwnCopy(new(3, "А"));
        Assert.Equal(CaptureResult.Ignored, policy.Observe(new(3, "А")));
        Assert.Equal(CaptureResult.Accepted, policy.Observe(new(4, "В")));
        Assert.Equal(CaptureResult.Accepted, policy.Observe(new(5, "А")));
    }

    [Fact]
    public void ResumeBaselineDoesNotCaptureTextCopiedOnPause()
    {
        var policy = new CapturePolicy();
        policy.Observe(new(1, "до паузы"));
        policy.EstablishBaseline(5);
        Assert.Equal(CaptureResult.Ignored, policy.Observe(new(5, "на паузе")));
        Assert.Equal(CaptureResult.Accepted, policy.Observe(new(6, "после паузы")));
    }
}
