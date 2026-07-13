using System.Drawing;
using System.Drawing.Imaging;
using LocalTranslator.Core.Abstractions;
using LocalTranslator.Core.Models;

namespace LocalTranslator.Infrastructure.Services;

public sealed class DesktopScreenCaptureService : IScreenCaptureService
{
    public Task<byte[]> CapturePngAsync(
        ScreenRegion region,
        CancellationToken cancellationToken = default)
    {
        if (!region.IsValid)
        {
            throw new ArgumentException("截图区域无效。", nameof(region));
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(region.X, region.Y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return Task.FromResult(stream.ToArray());
    }
}

