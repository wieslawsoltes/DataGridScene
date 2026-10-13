using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;
using System.Runtime.InteropServices.JavaScript;

namespace DataGridScene.Demo;

public static partial class BrowserBridge
{
    /// <summary>Read only the currently focused native editor and its real selection.
    /// No focus, template/layout forcing, handlers, input writes or model scans.</summary>
    [JSExport]
    public static string SnapshotRowEditorInput(string field)
    {
        var form = Page.DataGrid.ActiveRowEditForm;
        var root = Page.DataGrid.XamlRoot;
        var focused = root is null ? null : FocusManager.GetFocusedElement(root) as DependencyObject;
        FrameworkElement? editor = null;
        if (form is not null) form.Editors.TryGetValue(field, out editor);
        bool within = false;
        for (var node = focused; node is not null && editor is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, editor)) { within = true; break; }
        var text = within ? focused as TextBox : null;
        return JsonSerializer.Serialize(new {
            field, open = form is not null, within, focusedId = focused is null ? null : AutomationProperties.GetAutomationId(focused),
            focusedType = focused?.GetType().Name, text = text?.Text,
            selectionStart = text?.SelectionStart, selectionLength = text?.SelectionLength,
            readOnly = text?.IsReadOnly, enabled = text?.IsEnabled
        }, Json);
    }
}
