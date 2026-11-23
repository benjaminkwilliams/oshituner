using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace oshituner;

internal sealed class ThemeImages(InstallProfile profile)
{
    private const int MaxInput = 32 * 1024 * 1024;
    private const long MaxDecoded = 224L * 1024 * 1024;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public const string BuiltIn = "vtuber-4k";

    public static bool IsAssetId(string value) =>
        value == BuiltIn || value.Length == 64 && value.All(char.IsAsciiHexDigitLower);

    public async Task<string> ImportAsync(string source, CancellationToken cancellationToken)
    {
        source = source.Trim();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            var token = timeout.Token;
            byte[] bytes;
            if (Uri.TryCreate(source, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
            {
                if (url.UserInfo.Length != 0) throw new ArgumentException("Enter an image URL without embedded credentials.");
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxInput) throw new InvalidDataException("The image file is too large.");
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                bytes = await ReadBoundedAsync(stream, token);
            }
            else
            {
                if (!Path.IsPathFullyQualified(source)) throw new ArgumentException("Enter a local path, UNC path, or HTTP/HTTPS image URL.");
                bytes = await Task.Run(() => ReadFileAsync(source, token), token).WaitAsync(token);
            }

            await Task.Run(() => Decode(bytes, materialize: false, token), token);
            var id = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var path = AssetPath(id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(staging, bytes, token);
                token.ThrowIfCancellationRequested();
                File.Move(staging, path, overwrite: true);
            }
            finally { File.Delete(staging); }
            return id;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The background image took too long to load.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("Could not download the background image.");
        }
    }

    public Task<ThemeAnimation> LoadAsync(string assetId, CancellationToken cancellationToken)
    {
        if (!IsAssetId(assetId)) throw new ArgumentException("Unknown background image asset.");
        var path = assetId == BuiltIn
            ? Path.Combine(AppContext.BaseDirectory, "static", "images", "vtuber-viewer-background-4k-v2.png")
            : AssetPath(assetId);
        return Task.Run(async () =>
        {
            var bytes = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (assetId != BuiltIn && !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), assetId, StringComparison.Ordinal))
                throw new InvalidDataException("The cached background image is damaged.");
            return Decode(bytes, materialize: true, cancellationToken);
        }, cancellationToken);
    }

    private string AssetPath(string id) => Path.Combine(profile.DirectoryPath, "theme-images", id + ".image");

    private static async Task<byte[]> ReadFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxInput) throw new InvalidDataException("The image file is too large.");
        return await ReadBoundedAsync(stream, token).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaxInput) throw new InvalidDataException("The image file is too large.");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    private static ThemeAnimation Decode(byte[] bytes, bool materialize, CancellationToken token)
    {
        using var input = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(input);
        if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Gif or SKEncodedImageFormat.Webp
            or SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Bmp))
            throw new InvalidDataException("Choose a valid PNG, JPEG, BMP, GIF, or WebP image.");

        var width = codec.Info.Width;
        var height = codec.Info.Height;
        var count = Math.Max(1, codec.FrameCount);
        var decodedBytes = (long)width * height * 4 * count;
        if (width is <= 0 or > 8192 || height is <= 0 or > 8192 || count > 1000 || decodedBytes > MaxDecoded)
            throw new InvalidDataException("The image dimensions or animation are too large.");

        var frames = new List<ThemeFrame>(materialize ? count : 0);
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        for (var index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            var metadata = default(SKCodecFrameInfo);
            var hasMetadata = count > 1 && codec.GetFrameInfo(index, out metadata);
            var options = hasMetadata && index > 0 && metadata.RequiredFrame == index - 1
                ? new SKCodecOptions(index, index - 1) : new SKCodecOptions(index);
            if (options.PriorFrame < 0) bitmap.Erase(SKColors.Transparent);
            if (codec.GetPixels(info, bitmap.GetPixels(), bitmap.RowBytes, options) != SKCodecResult.Success)
                throw new InvalidDataException("The background image contains an incomplete or corrupt frame.");
            if (!materialize) continue;
            var pixels = new byte[bitmap.RowBytes * height];
            Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, bitmap.RowBytes);
            source.Freeze();
            frames.Add(new ThemeFrame(source, TimeSpan.FromMilliseconds(metadata.Duration > 0 ? metadata.Duration : 100)));
        }
        return new(frames.ToArray(), codec.RepetitionCount);
    }
}

internal sealed record ThemeFrame(BitmapSource Image, TimeSpan Duration);
internal sealed record ThemeAnimation(ThemeFrame[] Frames, int Repetitions);
