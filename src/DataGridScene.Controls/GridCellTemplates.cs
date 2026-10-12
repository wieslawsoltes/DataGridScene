using DataGridScene.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Globalization;
using System.Runtime.CompilerServices;
using Windows.System;

namespace DataGridScene.Controls;

/// <summary>Normal Uno controls with guarded binding and edits through the grid's history.
/// Text/number edits commit on Enter, focus loss or a grid operation; checks/choices commit
/// on activation, sliders on release. Binding/recycling never writes data.</summary>
public static class GridCellTemplates
{
    private sealed class State { public bool Binding; public string? NumberText; public SolidColorBrush? Background; }
    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();
    private static GridCellEditBuffer? Buffer(FrameworkElement element) =>
        !States.GetOrCreateValue(element).Binding && element.DataContext is GridCellEditBuffer { IsActive: true } buffer ? buffer : null;
    private static void Bind(FrameworkElement element, Action action)
    {
        var state = States.GetOrCreateValue(element); state.Binding = true;
        try { action(); } finally { state.Binding = false; }
    }
    private static string Format(GridCellEditBuffer buffer) => buffer.Value switch
    {
        DateTime value => value.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset value => value.ToString("O", CultureInfo.InvariantCulture),
        _ => Convert.ToString(buffer.Value, buffer.Culture) ?? ""
    };
    public static GridCellTemplate TextBox() => new(() =>
    {
        var control = new TextBox { MinWidth = 0, MinHeight = 0, Padding = new Thickness(6,2,6,2), FontSize = 12,
            BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), CornerRadius = new CornerRadius(0), VerticalContentAlignment = VerticalAlignment.Center };
        control.TextChanged += (_, _) => { if (Buffer(control) is { } buffer && control.Text != Format(buffer)) buffer.Value = control.Text; };
        return control;
    }) { Bind = (element, buffer) => Bind(element, () => {
        var control = (TextBox)element; string text = Format(buffer);
        if (control.Text != text) control.Text = text; control.IsReadOnly = !buffer.CanEdit;
    }), UpdateAppearance = UpdateEditorAppearance };

    public static GridCellTemplate CheckBox() => new(() =>
    {
        var control = new CheckBox { MinWidth = 0, MinHeight = 0, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        control.Click += (_, _) => { if (Buffer(control) is { } buffer) { buffer.Value = control.IsChecked; buffer.TryCommit(); } };
        return control;
    }) { Bind = (element, buffer) => Bind(element, () => {
        var control = (CheckBox)element; control.IsThreeState = buffer.Column.AllowNull;
        control.IsChecked = buffer.Value as bool?; control.IsEnabled = buffer.CanEdit;
    }) };

    public static GridCellTemplate ComboBox(IEnumerable<string> choices)
    {
        ArgumentNullException.ThrowIfNull(choices); var items = choices.ToArray();
        return new(() =>
        {
            var control = new ComboBox { ItemsSource = items, MinWidth = 0, MinHeight = 0, FontSize = 12,
                Padding = new Thickness(6,2,4,2), HorizontalAlignment = HorizontalAlignment.Stretch,
                BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), CornerRadius = new CornerRadius(0), VerticalContentAlignment = VerticalAlignment.Center };
            control.SelectionChanged += (_, _) => {
                if (Buffer(control) is { } buffer && !Equals(control.SelectedItem, buffer.Value))
                { buffer.Value = control.SelectedItem; buffer.TryCommit(); }
            };
            return control;
        }) { Bind = (element, buffer) => Bind(element, () => {
            var control = (ComboBox)element; control.SelectedItem = buffer.Value; control.IsEnabled = buffer.CanEdit;
        }), UpdateAppearance = UpdateEditorAppearance, Unbind = (element, _) => ((ComboBox)element).IsDropDownOpen = false };
    }
    public static GridCellTemplate Button(string caption, Action<GridCellEditBuffer> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return new(() =>
        {
            var control = new Button { Content = caption, MinWidth = 0, MinHeight = 0, FontSize = 12,
                Padding = new Thickness(8,1,8,1), Margin = new Thickness(3,2,3,2), CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Stretch };
            control.Click += (_, _) => { if (Buffer(control) is { } buffer) buffer.Invoke(action); };
            return control;
        });
    }
    public static GridCellTemplate ProgressBar(double minimum = 0, double maximum = 100)
    {
        ValidateRange(minimum, maximum);
        return new(() => new ProgressBar { Minimum = minimum, Maximum = maximum, Height = 6, Margin = new Thickness(8,0,8,0), VerticalAlignment = VerticalAlignment.Center })
        { Bind = (element, buffer) => Bind(element, () => ((ProgressBar)element).Value = Number(buffer.Value, minimum, maximum)) };
    }
    public static GridCellTemplate Slider(double minimum = 0, double maximum = 100, double step = 1)
    {
        ValidateRange(minimum, maximum); if (!double.IsFinite(step) || step <= 0) throw new ArgumentOutOfRangeException(nameof(step));
        return new(() =>
        {
            var control = new Slider { Minimum = minimum, Maximum = maximum, StepFrequency = step, MinHeight = 0, Margin = new Thickness(6,0,6,0), VerticalAlignment = VerticalAlignment.Center };
            control.ValueChanged += (_, _) => { if (Buffer(control) is { } buffer && control.Value != Number(buffer.Value, minimum, maximum)) buffer.Value = control.Value; };
            control.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => Buffer(control)?.TryCommit()), true);
            control.KeyUp += (_, args) => { if (args.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End) Buffer(control)?.TryCommit(); };
            return control;
        }) { Bind = (element, buffer) => Bind(element, () => {
            var control = (Slider)element; control.Value = Number(buffer.Value, minimum, maximum); control.IsEnabled = buffer.CanEdit;
        }) };
    }
    public static GridCellTemplate NumberBox(double minimum = double.MinValue, double maximum = double.MaxValue, double step = 1)
    {
        ValidateRange(minimum, maximum); if (!double.IsFinite(step) || step <= 0) throw new ArgumentOutOfRangeException(nameof(step));
        return new(() =>
        {
            var control = new NumberBox { Minimum = minimum, Maximum = maximum, SmallChange = step, MinWidth = 0, MinHeight = 0,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, ValidationMode = NumberBoxValidationMode.Disabled, FontSize = 12,
                BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            control.ValueChanged += (_, _) => {
                if (Buffer(control) is { } buffer && !(double.IsNaN(control.Value) && buffer.Value is null) && !SameNumber(buffer.Value, control.Value, buffer.Culture))
                    buffer.Value = double.IsNaN(control.Value) ? null : control.Value;
            };
            return control;
        }) { Bind = (element, buffer) => Bind(element, () => {
            var control = (NumberBox)element;
            if (!buffer.IsDirty)
            {
                control.Value = buffer.Value is null ? double.NaN : Number(buffer.Value, minimum, maximum);
                // Restore invalid input on Cancel even if the numeric Value did not change.
                control.Text = buffer.Value is null ? "" : control.NumberFormatter.FormatDouble(control.Value);
                States.GetOrCreateValue(control).NumberText = control.Text;
            }
            control.IsEnabled = buffer.CanEdit;
        }), UpdateAppearance = UpdateEditorAppearance, Flush = (element, buffer) => {
            var control = (NumberBox)element;
            // Compare against the actual formatted binding, not the exact source value:
            // an untouched double-based display must not round or clamp stored decimals.
            string? expected = States.GetOrCreateValue(control).NumberText;
            if (!string.Equals(control.Text, expected, StringComparison.Ordinal) && control.IsEnabled)
                buffer.Value = control.Text;
        } };
    }
    public static GridCellTemplate CalendarDatePicker() => new(() =>
    {
        var control = new CalendarDatePicker { MinWidth = 0, MinHeight = 0, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch, DateFormat = "{year.full}-{month.integer(2)}-{day.integer(2)}" };
        control.DateChanged += (_, _) => {
            if (Buffer(control) is not { } buffer) return;
            DateTime? current = buffer.Value is DateTime date ? date.Date : buffer.Value is DateTimeOffset offset ? offset.Date : null;
            if (current == control.Date?.Date) return;
            object? value = control.Date;
            if (control.Date is { } selected)
                value = buffer.Column.DataType == typeof(DateTime) ? selected.Date : new DateTimeOffset(selected.Date, buffer.Value is DateTimeOffset original ? original.Offset : TimeSpan.Zero);
            buffer.Value = value; buffer.TryCommit();
        };
        return control;
    }) { Bind = (element, buffer) => Bind(element, () => {
        var control = (CalendarDatePicker)element;
        control.Date = buffer.Value is DateTime date ? new DateTimeOffset(date) : buffer.Value as DateTimeOffset?;
        control.IsEnabled = buffer.CanEdit;
    }), Unbind = (element, _) => ((CalendarDatePicker)element).IsCalendarOpen = false };

    private static void UpdateEditorAppearance(FrameworkElement element, GridCellVisualState state)
    {
        if (element is not Control control) return;
        // Native controls cover the canvas background while focused. Keep the same
        // selection tint as the grid and let the host draw the focus outline above it.
        var color = state.IsSelected
            ? state.IsDark ? Windows.UI.Color.FromArgb(255, 40, 72, 100) : Windows.UI.Color.FromArgb(255, 204, 228, 247)
            : Microsoft.UI.Colors.Transparent;
        var binding = States.GetOrCreateValue(element);
        binding.Background ??= new SolidColorBrush(color);
        if (binding.Background.Color != color) binding.Background.Color = color;
        if (!ReferenceEquals(control.Background, binding.Background)) control.Background = binding.Background;
    }
    private static bool SameNumber(object? value, double number, CultureInfo culture)
    {
        if (value is null) return double.IsNaN(number);
        try { return Convert.ToDouble(value, culture) == number; }
        catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException) { return false; }
    }
    private static void ValidateRange(double minimum, double maximum)
    { if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum) throw new ArgumentOutOfRangeException(nameof(maximum)); }
    private static double Number(object? value, double minimum, double maximum)
    {
        double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        return double.IsFinite(number) ? Math.Clamp(number, minimum, maximum) : minimum;
    }
}
