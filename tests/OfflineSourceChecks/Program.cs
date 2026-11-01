using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChatGptSkinStudio;

internal static class Checks
{
    private static int passed;
    [STAThread]
    private static void Main()
    {
        byte[] Decode(byte[] bytes, int format, int w = 4, int h = 4) => WallpaperTexture.DecodePixels(bytes, format, w, h, default);
        Check("LZ4 literal", () => Equal(WallpaperTexture.ExpandLz4(new byte[] { 0x50, 104, 101, 108, 108, 111 }, 5, default), Encoding.ASCII.GetBytes("hello")));
        Check("LZ4 overlapping match", () => Equal(WallpaperTexture.ExpandLz4(new byte[] { 0x11, 65, 1, 0 }, 6, default), Encoding.ASCII.GetBytes("AAAAAA")));
        Check("LZ4 extension", () => Equal(WallpaperTexture.ExpandLz4(new byte[] { 0xf0, 1 }.Concat(Enumerable.Repeat((byte)7, 16)).ToArray(), 16, default), Enumerable.Repeat((byte)7, 16).ToArray()));
        Check("LZ4 invalid zero offset", () => Reject(() => WallpaperTexture.ExpandLz4(new byte[] { 0, 0, 0 }, 4, default)));
        Check("LZ4 truncated literal", () => Reject(() => WallpaperTexture.ExpandLz4(new byte[] { 0x50, 65 }, 5, default)));
        Check("LZ4 invalid expansion length", () => Reject(() => WallpaperTexture.ExpandLz4(new byte[] { 0xf0, 255 }, int.MaxValue, default)));
        Check("LZ4 cancellation", () => { using var stop = new CancellationTokenSource(); stop.Cancel(); try { WallpaperTexture.ExpandLz4(new byte[] { 0x10, 1 }, 1, stop.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { } });
        Check("RGBA channel order", () => Equal(Decode(new byte[] { 1, 2, 3, 4 }, 0, 1, 1), new byte[] { 3, 2, 1, 4 }));
        Check("R8 opaque gray", () => Equal(Decode(new byte[] { 30 }, 9, 1, 1), new byte[] { 30, 30, 30, 255 }));
        Check("RG88 alpha/gray", () => Equal(Decode(new byte[] { 10, 90 }, 8, 1, 1), new byte[] { 90, 90, 90, 10 }));
        Check("DXT1 red block", () => Equal(Decode(new byte[] { 0, 248, 31, 0, 0, 0, 0, 0 }, 7), Enumerable.Range(0, 16).SelectMany(_ => new byte[] { 0, 0, 255, 255 }).ToArray()));
        Check("DXT1 transparent code", () => Equal(Decode(new byte[] { 0, 0, 255, 255, 255, 255, 255, 255 }, 7), new byte[64]));
        Check("DXT3 explicit alpha", () => { byte[] block = Enumerable.Repeat((byte)0x88, 8).Concat(new byte[] { 0, 248, 31, 0, 0, 0, 0, 0 }).ToArray(); if (Decode(block, 6)[3] != 136) throw new Exception("Wrong DXT3 alpha"); });
        Check("DXT5 alpha endpoint", () => { byte[] block = new byte[] { 200, 20, 0, 0, 0, 0, 0, 0, 0, 248, 31, 0, 0, 0, 0, 0 }; if (Decode(block, 4)[3] != 200) throw new Exception("Wrong DXT5 alpha"); });
        Check("DXT partial block", () => { var result = Decode(new byte[] { 0, 248, 31, 0, 0, 0, 0, 0 }, 7, 3, 2); if (result.Length != 24 || result[22] != 255) throw new Exception("Partial block mismatch"); });
        Check("Texture pixel size bound", () => Reject(() => Decode(Array.Empty<byte>(), 0, 16384, 16384)));
        Check("DXT length check", () => Reject(() => Decode(new byte[7], 7)));
        Check("TEX effective pixels remove storage padding", () => { var result = WallpaperTexture.Decode(Texture(0, 4, 4, 3, 2, Enumerable.Repeat(new byte[] { 10, 20, 30, 255 }, 16).SelectMany(p => p).ToArray()), default); if (result.Width != 3 || result.Height != 2) throw new Exception("Effective size mismatch"); using var source = new MemoryStream(result.Bytes); var frame = BitmapDecoder.Create(source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0]; var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0); byte[] pixel = new byte[24]; converted.CopyPixels(pixel, 12, 0); Equal(pixel[..4], new byte[] { 30, 20, 10, 255 }); });
        Check("TEX effective size cannot exceed data", () => Reject(() => WallpaperTexture.Decode(Texture(0, 1, 1, 2, 2, new byte[4]), default)));
        Check("TEX truncated bytes", () => Reject(() => WallpaperTexture.Decode(new byte[4], default)));
        Check("TEX size can be inspected without payload", () => { var header = Texture(0, 2048, 2048, 1920, 1080, Array.Empty<byte>())[..46]; var size = WallpaperTexture.PeekSize(header); if (size.Width != 1920 || size.Height != 1080) throw new Exception("Header inspection mismatch"); });
        string temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cgss-offline-checks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temporary);
        try
        {
            Check("PKG indexed read", () => { string file = System.IO.Path.Combine(temporary, "valid.pkg"); File.WriteAllBytes(file, Package("materials/background.png", new byte[] { 1, 2, 3 })); using var pkg = new WallpaperPackage(file); Equal(pkg.Read("materials/background.png", default), new byte[] { 1, 2, 3 }); });
            Check("PKG rejects traversal", () => { string file = System.IO.Path.Combine(temporary, "traversal.pkg"); File.WriteAllBytes(file, Package("../escape.png", new byte[] { 1 })); Reject(() => { using var pkg = new WallpaperPackage(file); }); });
            Check("PKG rejects entry bounds", () => { string file = System.IO.Path.Combine(temporary, "bounds.pkg"); File.WriteAllBytes(file, Package("file.tex", new byte[] { 1 }, 100)); Reject(() => { using var pkg = new WallpaperPackage(file); }); });
        }
        finally { foreach (string file in Directory.EnumerateFiles(temporary)) File.Delete(file); Directory.Delete(temporary); }
        Console.WriteLine($"Passed {passed} offline format checks.");
    }
    private static byte[] Texture(int format, int w, int h, int imageW, int imageH, byte[] payload)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        void Tag(string text) { writer.Write(Encoding.ASCII.GetBytes(text)); writer.Write((byte)0); }
        Tag("TEXV0005"); Tag("TEXI0001"); foreach (int value in new[] { format, 0, w, h, imageW, imageH, 0 }) writer.Write(value);
        Tag("TEXB0002"); foreach (int value in new[] { 1, 1, w, h, 0, 0, payload.Length }) writer.Write(value); writer.Write(payload); return stream.ToArray();
    }
    private static byte[] Package(string name, byte[] payload, int offset = 0)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        void Text(string text) { byte[] bytes = Encoding.UTF8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes); }
        Text("PKGV0024"); writer.Write(1); Text(name); writer.Write(offset); writer.Write(payload.Length); writer.Write(payload); return stream.ToArray();
    }
    private static void Check(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static void Equal(byte[] actual, byte[] expected) { if (!actual.AsSpan().SequenceEqual(expected)) throw new Exception("Byte mismatch"); }
    private static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException) { return; } throw new Exception("Malformed data accepted"); }
}
