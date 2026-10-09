using System.Text.Json;
using Uno.Foundation;
namespace DataGridScene.Demo;
public sealed partial class MainPage
{
    partial void InstallBrowserBridge()
    {
        WebAssemblyRuntime.InvokeJS("(async()=>{try{const runtime=globalThis.getDotnetRuntime(0);const assembly=await runtime.getAssemblyExports('DataGridScene.Demo');const api=assembly.DataGridScene.Demo.BrowserBridge;globalThis.dataGridScene={ready:true,snapshot:()=>JSON.parse(api.Snapshot()),command:(name,data={})=>JSON.parse(api.Command(name,JSON.stringify(data))),validate:async()=>JSON.parse(await api.Validate())};}catch(error){globalThis.dataGridSceneError=String(error);console.error(error);}})();");
    }
    partial void StoreLayout(string json) => WebAssemblyRuntime.InvokeJS("localStorage.setItem('DataGridScene.layout.v1'," + JsonSerializer.Serialize(json) + ");");
    private partial string? ReadLayout() => WebAssemblyRuntime.InvokeJS("localStorage.getItem('DataGridScene.layout.v1')||''");
    private partial void SaveFile(string name, byte[] bytes, string mime)
    {
        string data = Convert.ToBase64String(bytes);
        WebAssemblyRuntime.InvokeJS("(()=>{const binary=atob(" + JsonSerializer.Serialize(data) + ");const bytes=Uint8Array.from(binary,c=>c.charCodeAt(0));const url=URL.createObjectURL(new Blob([bytes],{type:" + JsonSerializer.Serialize(mime) + "}));const link=document.createElement('a');link.href=url;link.download=" + JsonSerializer.Serialize(name) + ";document.body.appendChild(link);link.click();link.remove();setTimeout(()=>URL.revokeObjectURL(url),10000);})();");
        SetMessage($"Exported {name} ({bytes.Length:N0} bytes).");
    }
}
