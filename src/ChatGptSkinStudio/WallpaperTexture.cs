using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChatGptSkinStudio;

internal sealed record DecodedWallpaper(byte[] Bytes, string Extension, int Width, int Height);

// TEX container facts follow RePKG's documented formats; decoding is independently implemented.
internal static class WallpaperTexture
{
    internal const long MaximumPixels = 64L * 1024 * 1024;
    internal static (int Width, int Height) PeekSize(byte[] header)
    {
        using var reader = new BinaryReader(new MemoryStream(header, false), Encoding.UTF8, false);
        if (Tag(reader, 16) != "TEXV0005" || Tag(reader, 16) != "TEXI0001") throw new InvalidDataException("不支持的TEX版本。");
        _ = reader.ReadInt32(); _ = reader.ReadInt32(); _ = reader.ReadInt32(); _ = reader.ReadInt32();
        int width = reader.ReadInt32(), height = reader.ReadInt32(); CheckSize(width, height); return (width, height);
    }
    internal static DecodedWallpaper Decode(byte[] bytes, CancellationToken token)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        if (Tag(reader, 16) != "TEXV0005" || Tag(reader, 16) != "TEXI0001") throw new InvalidDataException("不支持的TEX版本。");
        int format = reader.ReadInt32(); _ = reader.ReadInt32();
        int textureWidth = reader.ReadInt32(), textureHeight = reader.ReadInt32();
        int imageWidth = reader.ReadInt32(), imageHeight = reader.ReadInt32(); _ = reader.ReadInt32();
        CheckSize(textureWidth, textureHeight); CheckSize(imageWidth, imageHeight);
        string block = Tag(reader, 16);
        int version = block switch { "TEXB0001" => 1, "TEXB0002" => 2, "TEXB0003" => 3, "TEXB0004" => 4, _ => throw new InvalidDataException("不支持的TEX数据块。") };
        int images = reader.ReadInt32(); if (images < 1 || images > 4096) throw new InvalidDataException("TEX图像数量无效。");
        int embeddedFormat = version >= 3 ? reader.ReadInt32() : -1;
        bool video = version == 4 && reader.ReadInt32() == 1 && embeddedFormat == -1;
        int mipmaps = reader.ReadInt32(); if (mipmaps < 1 || mipmaps > 32) throw new InvalidDataException("TEX层级数量无效。");
        if (video)
        {
            if (reader.ReadInt32() != 1 || reader.ReadInt32() != 2) throw new InvalidDataException("不支持的TEX视频头。");
            _ = Tag(reader, 65536);
            if (reader.ReadInt32() != 1) throw new InvalidDataException("不支持的TEX视频数据。");
        }
        int width = reader.ReadInt32(), height = reader.ReadInt32();
        int compressed = version >= 2 ? reader.ReadInt32() : 0;
        int expanded = version >= 2 ? reader.ReadInt32() : 0;
        int length = reader.ReadInt32();
        if (length < 1 || length > WallpaperPackage.MaximumPayload || length > stream.Length - stream.Position || compressed is not (0 or 1)) throw new InvalidDataException("TEX数据长度无效。");
        byte[] payload = reader.ReadBytes(length);
        token.ThrowIfCancellationRequested();
        if (compressed == 1) payload = ExpandLz4(payload, expanded, token);
        if (video || (payload.Length >= 12 && Encoding.ASCII.GetString(payload, 4, 4) == "ftyp"))
            return new(payload, ".mp4", imageWidth, imageHeight);
        if (embeddedFormat != -1 || IsEncoded(payload)) return EncodeImage(payload, imageWidth, imageHeight);
        CheckSize(width, height);
        if (imageWidth > width || imageHeight > height) throw new InvalidDataException("TEX有效尺寸超出最高分辨率纹理。");
        byte[] rgba = DecodePixels(payload, format, width, height, token);
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, rgba, checked(width * 4));
        BitmapSource effective = width == imageWidth && height == imageHeight ? source : new CroppedBitmap(source, new Int32Rect(0, 0, imageWidth, imageHeight));
        return Png(effective);
    }
    internal static void CheckSize(int width, int height)
    {
        if (width < 1 || height < 1 || width > 16384 || height > 16384 || (long)width * height > MaximumPixels) throw new InvalidDataException("原图尺寸超出本版离线解码限制。");
    }
    internal static DecodedWallpaper EncodeImage(byte[] bytes, int expectedWidth = 0, int expectedHeight = 0)
    {
        using var stream = new MemoryStream(bytes, false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0]; CheckSize(frame.PixelWidth, frame.PixelHeight);
        if (expectedWidth > 0 && expectedHeight > 0 && (frame.PixelWidth != expectedWidth || frame.PixelHeight != expectedHeight))
        {
            if (frame.PixelWidth < expectedWidth || frame.PixelHeight < expectedHeight) throw new InvalidDataException("内嵌图片尺寸小于原图有效尺寸。");
            return Png(new CroppedBitmap(frame, new Int32Rect(0, 0, expectedWidth, expectedHeight)));
        }
        if (bytes.Length > 8 && bytes[0] == 137 && bytes[1] == 80) return new(bytes, ".png", frame.PixelWidth, frame.PixelHeight);
        if (bytes.Length > 3 && bytes[0] == 255 && bytes[1] == 216) return new(bytes, ".jpg", frame.PixelWidth, frame.PixelHeight);
        return Png(frame);
    }
    private static DecodedWallpaper Png(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output);
        return new(output.ToArray(), ".png", bitmap.PixelWidth, bitmap.PixelHeight);
    }
    private static bool IsEncoded(byte[] bytes) => bytes.Length > 8 && ((bytes[0] == 137 && bytes[1] == 80) || (bytes[0] == 255 && bytes[1] == 216) || (bytes[0] == 66 && bytes[1] == 77));
    private static string Tag(BinaryReader reader, int maximum)
    {
        var value = new StringBuilder();
        for (int i = 0; i <= maximum; i++) { byte b = reader.ReadByte(); if (b == 0) return value.ToString(); value.Append((char)b); }
        throw new InvalidDataException("TEX字符串超出限制。");
    }
    internal static byte[] ExpandLz4(byte[] input, int length, CancellationToken token)
    {
        if (length < 1 || length > WallpaperPackage.MaximumPayload) throw new InvalidDataException("LZ4解压尺寸无效。");
        byte[] output = new byte[length]; int i = 0, o = 0;
        int Extend(int count) { if (count != 15) return count; int b; do { if (i >= input.Length) throw new InvalidDataException("LZ4长度不完整。"); b = input[i++]; count = checked(count + b); } while (b == 255); return count; }
        while (i < input.Length)
        {
            token.ThrowIfCancellationRequested(); byte tag = input[i++]; int literals = Extend(tag >> 4);
            if (literals > input.Length - i || literals > output.Length - o) throw new InvalidDataException("LZ4字面数据越界。");
            input.AsSpan(i, literals).CopyTo(output.AsSpan(o)); i += literals; o += literals;
            if (i == input.Length) break;
            if (i + 2 > input.Length) throw new InvalidDataException("LZ4引用不完整。");
            int offset = input[i] | input[i + 1] << 8; i += 2; int match = checked(Extend(tag & 15) + 4);
            if (offset < 1 || offset > o || match > output.Length - o) throw new InvalidDataException("LZ4引用越界。");
            for (int n = 0; n < match; n++) output[o + n] = output[o + n - offset]; o += match;
        }
        if (o != output.Length) throw new InvalidDataException("LZ4解压长度不匹配。");
        return output;
    }
    internal static byte[] DecodePixels(byte[] input, int format, int width, int height, CancellationToken token)
    {
        CheckSize(width, height); byte[] pixels = new byte[checked(width * height * 4)];
        if (format is 0 or 8 or 9)
        {
            int channels = format == 0 ? 4 : format == 8 ? 2 : 1;
            if ((long)width * height * channels != input.Length) throw new InvalidDataException("纹理像素长度不匹配。");
            for (int p = 0, s = 0; p < pixels.Length; p += 4, s += channels)
            {
                if ((p & 65535) == 0) token.ThrowIfCancellationRequested();
                if (format == 0) { pixels[p] = input[s + 2]; pixels[p + 1] = input[s + 1]; pixels[p + 2] = input[s]; pixels[p + 3] = input[s + 3]; }
                else { pixels[p] = pixels[p + 1] = pixels[p + 2] = input[s + channels - 1]; pixels[p + 3] = channels == 2 ? input[s] : (byte)255; }
            }
            return pixels;
        }
        int blockSize = format == 7 ? 8 : format is 4 or 6 ? 16 : throw new InvalidDataException("不支持的纹理压缩格式。");
        int columns = (width + 3) / 4, rows = (height + 3) / 4;
        if ((long)columns * rows * blockSize != input.Length) throw new InvalidDataException("DXT纹理数据长度不匹配。");
        Span<byte> palette = stackalloc byte[16]; Span<byte> alpha = stackalloc byte[8];
        for (int by = 0, start = 0; by < rows; by++) for (int bx = 0; bx < columns; bx++, start += blockSize)
        {
            if (bx == 0) token.ThrowIfCancellationRequested();
            var block = input.AsSpan(start, blockSize); int color = blockSize - 8;
            ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(block[color..]), c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[(color + 2)..]);
            Color(c0, palette[..4]); Color(c1, palette.Slice(4, 4));
            bool four = format != 7 || c0 > c1;
            for (int k = 0; k < 3; k++) { palette[8 + k] = (byte)(four ? (2 * palette[k] + palette[4 + k]) / 3 : (palette[k] + palette[4 + k]) / 2); palette[12 + k] = four ? (byte)((palette[k] + 2 * palette[4 + k]) / 3) : (byte)0; }
            palette[11] = 255; palette[15] = four ? (byte)255 : (byte)0;
            ulong alphaBits = 0;
            if (format == 4)
            {
                alpha[0] = block[0]; alpha[1] = block[1];
                if (alpha[0] > alpha[1]) for (int a = 2; a < 8; a++) alpha[a] = (byte)(((8 - a) * alpha[0] + (a - 1) * alpha[1]) / 7);
                else { for (int a = 2; a < 6; a++) alpha[a] = (byte)(((6 - a) * alpha[0] + (a - 1) * alpha[1]) / 5); alpha[6] = 0; alpha[7] = 255; }
                for (int b = 0; b < 6; b++) alphaBits |= (ulong)block[2 + b] << (8 * b);
            }
            uint indices = BinaryPrimitives.ReadUInt32LittleEndian(block[(color + 4)..]);
            for (int n = 0; n < 16; n++)
            {
                int x = bx * 4 + n % 4, y = by * 4 + n / 4; if (x >= width || y >= height) continue;
                int pixel = (y * width + x) * 4, selected = (int)((indices >> (2 * n)) & 3) * 4;
                palette.Slice(selected, 4).CopyTo(pixels.AsSpan(pixel));
                if (format == 4) pixels[pixel + 3] = alpha[(int)((alphaBits >> (3 * n)) & 7)];
                else if (format == 6) pixels[pixel + 3] = (byte)(((block[n / 2] >> (4 * (n & 1))) & 15) * 17);
            }
        }
        return pixels;
    }
    private static void Color(ushort color, Span<byte> output)
    {
        int r = color >> 11, g = color >> 5 & 63, b = color & 31;
        output[0] = (byte)((b << 3) | (b >> 2)); output[1] = (byte)((g << 2) | (g >> 4)); output[2] = (byte)((r << 3) | (r >> 2)); output[3] = 255;
    }
}
