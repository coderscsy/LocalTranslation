using LocalTranslator.Core.Models;

namespace LocalTranslator.Core.Abstractions;

public interface IScreenCaptureService
{
    Task<byte[]> CapturePngAsync(ScreenRegion region, CancellationToken cancellationToken = default);
}

