using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

namespace DataGridScene.Demo;

public static partial class BrowserBridge
{
    [JSExport]
    public static string ValidateNativeModes() => JsonSerializer.Serialize(NativeModeControlChecks.Run(), Json);
}
