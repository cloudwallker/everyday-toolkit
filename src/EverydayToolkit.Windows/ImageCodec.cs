using EverydayToolkit.Core;
using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace EverydayToolkit.Windows;

public sealed class ImageCodec
{
    internal const long MaxPixelsBytes = 64L * 1024 * 1024;
    internal const int MaxPngBytes = 8 * 1024 * 1024;
    internal static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixelsBytes / 4)
            throw new ContentValidationException("图像解码内存超过限制或尺寸无效。");
    }
    public ClipboardContent Encode(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateDimensions(source.PixelWidth, source.PixelHeight);
        BitmapSource normalized = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        normalized.Freeze();
        var png = Png(normalized);
        if (png.Length > MaxPngBytes) throw new ContentValidationException("PNG 超过 8 MiB 限制。");
        double scale = Math.Min(1, 256d / Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource thumbnail = scale < 1 ? new TransformedBitmap(normalized, new ScaleTransform(scale, scale)) : normalized;
        thumbnail.Freeze();
        return new ClipboardContent(EntryKind.Image, Png: png, Thumbnail: Png(thumbnail), Width: source.PixelWidth, Height: source.PixelHeight);
    }
    private static byte[] Png(BitmapSource source)
    {
        using var stream = new MemoryStream(); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source)); encoder.Save(stream); return stream.ToArray();
    }
    public BitmapSource Decode(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length > MaxPngBytes || png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}) ||
            BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8,4)) != 13 || !png.AsSpan(12,4).SequenceEqual("IHDR"u8))
            throw new ContentValidationException("PNG 数据无效或超过限制。");
        var width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16,4)); var height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20,4));
        if (width > int.MaxValue || height > int.MaxValue) throw new ContentValidationException("PNG 尺寸无效。");
        ValidateDimensions((int)width, (int)height);
        byte depth=png[24], color=png[25];
        int sourceBytesPerPixel=color switch
        {
            0 when depth is 1 or 2 or 4 or 8 or 16=>depth==16 ? 2 : 1,
            2 when depth is 8 or 16=>depth==16 ? 6 : 3,
            3 when depth is 1 or 2 or 4 or 8=>4,
            // WIC expands grayscale plus alpha to an RGBA frame.
            4 when depth is 8 or 16=>depth==16 ? 8 : 4,
            6 when depth is 8 or 16=>depth==16 ? 8 : 4,
            _=>throw new ContentValidationException("PNG 位深或颜色类型无效。")
        };
        if((long)width*height*sourceBytesPerPixel>MaxPixelsBytes)
            throw new ContentValidationException("PNG 原始解码内存超过 64 MiB 限制。");
        try
        {
            using var stream = new MemoryStream(png, false);
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame=decoder.Frames[0];
            ValidateDimensions(frame.PixelWidth,frame.PixelHeight);
            long sourceStride=((long)frame.PixelWidth*frame.Format.BitsPerPixel+7)/8;
            if(sourceStride*frame.PixelHeight>MaxPixelsBytes) throw new ContentValidationException("PNG 原始解码内存超过限制。");
            BitmapSource converted=frame.Format==PixelFormats.Bgra32 ? frame : new FormatConvertedBitmap(frame,PixelFormats.Bgra32,null,0);
            int stride=checked(frame.PixelWidth*4); var pixels=new byte[checked(stride*frame.PixelHeight)]; converted.CopyPixels(pixels,stride,0);
            // Cache a detached BGRA buffer, rather than retaining a conversion
            // graph which keeps the original high bit-depth decoder frame alive.
            var image=BitmapSource.Create(frame.PixelWidth,frame.PixelHeight,96,96,PixelFormats.Bgra32,null,pixels,stride);
            image.Freeze(); return image;
        }
        catch (ContentValidationException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or FormatException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { throw new ContentValidationException("PNG 数据无法解码。"); }
    }
}
