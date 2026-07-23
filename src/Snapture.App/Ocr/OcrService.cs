using Snapture.Core.Capture;
using Snapture.Core.Models;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Snapture.App.Ocr;

/// <summary>Outcome of a "copy text" capture.</summary>
internal sealed record TextCaptureResult(bool Success, string? Text, string? Error);

/// <summary>
/// Captures a screen region (a plain single-shot GDI grab, like a snapshot) and
/// runs Windows' built-in OCR engine on it. The engine recognizes small images
/// poorly or not at all, so a selection under <see cref="MinDimension"/> on
/// either axis is padded with black on all sides before recognition — this is
/// the "minimal region size" annoyance the built-in Windows text-recognition
/// flows have that Snapture works around.
/// </summary>
internal static class OcrService
{
    private const int MinDimension = 64;

    public static async Task<TextCaptureResult> CaptureAndRecognizeAsync(CaptureTarget target)
    {
        byte[] pixels;
        int width, height;
        try
        {
            var region = target.Region.ToEvenDimensions();
            if (region.IsEmpty)
                return new TextCaptureResult(false, null, "Capture region is too small.");

            using var source = new GdiFrameSource(target with { Region = region }, captureCursor: false);
            using var frame = source.Capture(TimeSpan.Zero);
            if (frame is null)
                return new TextCaptureResult(false, null, "Failed to capture the screen.");
            width = source.Width;
            height = source.Height;
            pixels = frame.Pixels.ToArray();
        }
        catch (Exception ex)
        {
            return new TextCaptureResult(false, null, ex.Message);
        }

        (pixels, width, height) = PadToMinimum(pixels, width, height);

        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
            return new TextCaptureResult(false, null,
                "No OCR language is installed. Add one under Windows Settings → Time & language → Language & region.");

        try
        {
            var writer = new DataWriter();
            writer.WriteBytes(pixels);
            var buffer = writer.DetachBuffer();
            var bitmap = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);

            var result = await engine.RecognizeAsync(bitmap);
            var text = result.Text;
            return string.IsNullOrWhiteSpace(text)
                ? new TextCaptureResult(false, null, "No text was recognized in the selected area.")
                : new TextCaptureResult(true, text, null);
        }
        catch (Exception ex)
        {
            return new TextCaptureResult(false, null, ex.Message);
        }
    }

    /// <summary>Pad the tightly-packed BGRA buffer with black, centring the original pixels.</summary>
    private static (byte[] Pixels, int Width, int Height) PadToMinimum(byte[] src, int width, int height)
    {
        if (width >= MinDimension && height >= MinDimension)
            return (src, width, height);

        int newWidth = Math.Max(width, MinDimension);
        int newHeight = Math.Max(height, MinDimension);
        var dst = new byte[newWidth * newHeight * 4]; // zero-filled: opaque-ignored black
        int offsetX = (newWidth - width) / 2;
        int offsetY = (newHeight - height) / 2;
        int srcStride = width * 4, dstStride = newWidth * 4;
        for (int y = 0; y < height; y++)
            System.Buffer.BlockCopy(src, y * srcStride, dst, (y + offsetY) * dstStride + offsetX * 4, srcStride);
        return (dst, newWidth, newHeight);
    }
}
