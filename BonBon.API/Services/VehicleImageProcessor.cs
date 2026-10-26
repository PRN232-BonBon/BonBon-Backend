using BonBon.DataAccess.Repositories.Interfaces;
using SkiaSharp;

namespace BonBon.API.Services;

public sealed class VehicleImageProcessor
{
    private const long MaxSourceBytes = 10 * 1024 * 1024;
    private const int MaxOutputBytes = 2 * 1024 * 1024;
    private const int MaxDimension = 1600;

    public async Task<StoredImage> ProcessAsync(IFormFile file, CancellationToken ct)
    {
        if (file.Length is <= 0 or > MaxSourceBytes)
            throw new InvalidDataException("Mỗi ảnh phải nhỏ hơn 10MB.");
        if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Tệp tải lên phải là hình ảnh.");

        await using var input = new MemoryStream();
        await file.CopyToAsync(input, ct);
        input.Position = 0;

        using var data = SKData.Create(input);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("Không thể đọc nội dung ảnh.");
        if ((long)codec.Info.Width * codec.Info.Height > 40_000_000)
            throw new InvalidDataException("Độ phân giải ảnh quá lớn.");

        using var decoded = SKBitmap.Decode(data) ?? throw new InvalidDataException("Không thể giải mã ảnh.");
        using var oriented = ApplyOrientation(decoded, codec.EncodedOrigin);
        var scale = Math.Min(1d, (double)MaxDimension / Math.Max(oriented.Width, oriented.Height));
        var outputWidth = Math.Max(1, (int)Math.Round(oriented.Width * scale));
        var outputHeight = Math.Max(1, (int)Math.Round(oriented.Height * scale));

        using var resized = new SKBitmap(new SKImageInfo(outputWidth, outputHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(resized))
        {
            canvas.Clear(SKColors.Transparent);
            using var orientedImage = SKImage.FromBitmap(oriented);
            canvas.DrawImage(orientedImage, new SKRect(0, 0, outputWidth, outputHeight),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }

        var bytes = Encode(resized, 78);
        if (bytes.Length > MaxOutputBytes) bytes = Encode(resized, 62);
        if (bytes.Length > MaxOutputBytes)
            throw new InvalidDataException("Ảnh sau tối ưu vẫn vượt quá 2MB.");

        return new StoredImage(bytes, outputWidth, outputHeight);
    }

    private static byte[] Encode(SKBitmap bitmap, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, quality)
            ?? throw new InvalidDataException("Không thể tối ưu ảnh.");
        return encoded.ToArray();
    }

    private static SKBitmap ApplyOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        var swapsAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(
            swapsAxes ? source.Height : source.Width,
            swapsAxes ? source.Width : source.Height,
            source.ColorType,
            source.AlphaType);

        using var canvas = new SKCanvas(result);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(source.Width, 0); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(source.Width, source.Height); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, source.Height); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop:
                canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(source.Height, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(source.Height, source.Width); canvas.Scale(-1, 1); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, source.Width); canvas.RotateDegrees(-90); break;
        }
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }
}
