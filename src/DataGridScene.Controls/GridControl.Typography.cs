using SkiaSharp;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DataGridScene.Controls;

public sealed partial class GridControl
{
    /// <summary>Loads a caller-selected app-package typeface into the independent renderer.</summary>
    public async Task LoadTypefaceAsync(Uri applicationUri, float fontSize = 13)
    {
        ArgumentNullException.ThrowIfNull(applicationUri);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (applicationUri.Scheme != "ms-appx") throw new ArgumentException("An ms-appx package URI is required.", nameof(applicationUri));
        var file = await StorageFile.GetFileFromApplicationUriAsync(applicationUri);
        var buffer = await FileIO.ReadBufferAsync(file);
        if (buffer.Length > 32 * 1024 * 1024) throw new InvalidDataException("Typeface exceeds 32 MiB.");
        byte[] bytes = new byte[checked((int)buffer.Length)];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
        using var data = SKData.CreateCopy(bytes);
        using var face = SKTypeface.FromData(data) ?? throw new InvalidDataException("The package resource is not a supported typeface.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        Renderer.SetTypeface(face, fontSize); Redraw();
    }
}
