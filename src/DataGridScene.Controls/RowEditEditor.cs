using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Globalization;

namespace DataGridScene.Controls;

/// <summary>A field snapshot, not a live table or row buffer. Factories initialize
/// controls from InitialValue; the form captures pending input only on Update.</summary>
public sealed record RowEditEditorContext(long RecordId, string Field, string Caption, Type DataType,
    bool AllowNull, bool IsReadOnly, object? InitialValue, CultureInfo Culture, IReadOnlyList<string> Choices);

/// <summary>One native field editor with explicit pending-value, permission and
/// cleanup contracts. Instances and their elements belong to at most one form.
/// Callbacks run synchronously on the UI owner and must not mutate the table/form.</summary>
public sealed class RowEditEditor : IDisposable
{
    private Func<object?>? _read;
    private Action<bool>? _readOnly;
    private Action? _release;
    private bool _claimed, _disposed;
    private bool? _lastReadOnly;
    public FrameworkElement Element { get; }
    public Control? FocusTarget { get; }
    public bool UseNullToggle { get; }
    public RowEditEditor(FrameworkElement element, Func<object?> readValue, Action<bool> setReadOnly,
        Action? release = null, Control? focusTarget = null, bool useNullToggle = true)
    {
        ArgumentNullException.ThrowIfNull(element); ArgumentNullException.ThrowIfNull(readValue); ArgumentNullException.ThrowIfNull(setReadOnly);
        Element = element; _read = readValue; _readOnly = setReadOnly; _release = release;
        FocusTarget = focusTarget ?? element as Control; UseNullToggle = useNullToggle;
    }
    internal void Claim()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_claimed) throw new InvalidOperationException("An editor instance can belong to only one row form.");
        if (Element.IsLoaded || VisualTreeHelper.GetParent(Element) is not null)
            throw new InvalidOperationException("A row editor must return a new, unparented element.");
        if (FocusTarget is not null)
        {
            DependencyObject? current = FocusTarget;
            while (current is not null && !ReferenceEquals(current, Element)) current = VisualTreeHelper.GetParent(current);
            if (current is null) throw new ArgumentException("The focus target must be the editor or one of its descendants.");
        }
        _claimed = true;
    }
    internal object? ReadValue()
    {
        ObjectDisposedException.ThrowIf(_disposed, this); return _read!();
    }
    internal bool RefreshReadOnly(bool readOnly)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lastReadOnly == readOnly) return false;
        try { _readOnly!(readOnly); _lastReadOnly = readOnly; return true; }
        catch
        {
            // The adapter may have changed part of its control tree before throwing.
            // Even returning to the previously successful state must reapply it.
            _lastReadOnly = null; throw;
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var release = _release; _read = null; _readOnly = null; _release = null;
        release?.Invoke();
    }
}

/// <summary>Native editors usable by both standalone and grid-hosted row forms.</summary>
public static class RowEditEditors
{
    public static RowEditEditor Text(RowEditEditorContext context, bool multiline = false, double minimumHeight = 32)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!double.IsFinite(minimumHeight) || minimumHeight is < 24 or > 400) throw new ArgumentOutOfRangeException(nameof(minimumHeight));
        if (multiline && context.DataType != typeof(string)) throw new ArgumentException("Multiline editors require a string field.", nameof(context));
        var initial = Format(context.InitialValue, context.Culture);
        var text = new TextBox { Text = initial, MinWidth = 0, MinHeight = minimumHeight, FontSize = 12,
            Padding = new Thickness(7, 4, 7, 4), IsReadOnly = context.IsReadOnly,
            HorizontalAlignment = HorizontalAlignment.Stretch, AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
        return new(text, () => text.Text == initial && context.InitialValue is not null ? context.InitialValue : text.Text,
            readOnly => { if (text.IsReadOnly != readOnly) text.IsReadOnly = readOnly; });
    }
    /// <summary>Load a fresh DataTemplate root. Applications supply capture and
    /// read-only adapters for their template, rather than writing the model in bindings.</summary>
    public static RowEditEditor FromTemplate(DataTemplate template, RowEditEditorContext context,
        Func<FrameworkElement, object?> readValue, Action<FrameworkElement, bool> setReadOnly,
        Action<FrameworkElement>? release = null)
    {
        ArgumentNullException.ThrowIfNull(template); ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(readValue); ArgumentNullException.ThrowIfNull(setReadOnly);
        if (template.LoadContent() is not FrameworkElement element) throw new ArgumentException("A row editor template must produce a FrameworkElement.", nameof(template));
        element.DataContext = context;
        return new(element, () => readValue(element), readOnly => setReadOnly(element, readOnly), () => release?.Invoke(element));
    }
    internal static RowEditEditor Default(RowEditEditorContext context)
    {
        if (context.DataType == typeof(bool) && context.InitialValue is null or bool)
        {
            var check = new CheckBox { IsThreeState = context.AllowNull, IsChecked = context.InitialValue as bool?, MinHeight = 32 };
            return new(check, () => check.IsChecked, readOnly => check.IsEnabled = !readOnly, useNullToggle: false);
        }
        if (context.DataType == typeof(string) && context.Choices.Count > 0 && context.InitialValue is null or string)
        {
            var items = context.Choices.ToList();
            if (context.InitialValue is string current && !items.Contains(current)) items.Add(current);
            var choice = new ComboBox { ItemsSource = items, SelectedItem = context.InitialValue, MinWidth = 0, MinHeight = 32,
                HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
            return new(choice, () => choice.SelectedItem, readOnly => choice.IsEnabled = !readOnly);
        }
        return Text(context);
    }
    private static string Format(object? value, CultureInfo culture) => value switch
    {
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, culture) ?? ""
    };
}
