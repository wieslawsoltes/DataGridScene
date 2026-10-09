using Windows.Storage;
using Windows.Storage.Pickers;
namespace DataGridScene.Demo;
public sealed partial class MainPage
{
    partial void StoreLayout(string json)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DataGridScene"); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "layout.json"), json);
    }
    private partial string? ReadLayout()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DataGridScene", "layout.json"); return File.Exists(path) ? File.ReadAllText(path) : null;
    }
    private async partial void SaveFile(string name, byte[] bytes, string mime)
    {
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(name) };
            picker.FileTypeChoices.Add("Export", new List<string> { Path.GetExtension(name) });
            var file = await picker.PickSaveFileAsync(); if (file is not null) { await FileIO.WriteBytesAsync(file, bytes); SetMessage($"Saved {name}."); }
        }
        catch (Exception error) { SetMessage(error.Message); }
    }
}
