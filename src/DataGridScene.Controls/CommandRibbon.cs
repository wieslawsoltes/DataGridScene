using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DataGridScene.Controls;

public sealed record RibbonCommand(string Id, string Caption, SceneGlyph Icon, Action Execute, string? ToolTip = null);

/// <summary>A reusable compact command ribbon. Groups scroll horizontally at narrow widths;
/// all commands are native keyboard-focusable buttons. No demo data or grid dependency.</summary>
public sealed class CommandRibbon : UserControl
{
    private readonly StackPanel _groups = new() { Orientation = Orientation.Horizontal, Spacing = 0 };
    private readonly Border _border;
    private readonly List<VectorIcon> _icons = [];
    private readonly List<Border> _dividers = [];
    private readonly Dictionary<string, Button> _commands = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, Button> Commands => new System.Collections.ObjectModel.ReadOnlyDictionary<string, Button>(_commands);
    public event EventHandler<Exception>? CommandFailed;
    public CommandRibbon()
    {
        IsTabStop = false;
        _border = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Padding = new Thickness(4, 2, 4, 2),
            Child = new ScrollViewer { Content = _groups, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        Content = _border; SetTheme(false);
    }
    public void AddGroup(string caption, params RibbonCommand[] commands)
    {
        if (commands.Length == 0) throw new ArgumentException("A group must contain commands.", nameof(commands));
        if (commands.Any(command => string.IsNullOrWhiteSpace(command.Id) || command.Execute is null) || commands.Select(command => command.Id).Distinct().Count() != commands.Length || commands.Any(command => _commands.ContainsKey(command.Id)))
            throw new ArgumentException("Ribbon command IDs must be unique and executable.", nameof(commands));
        var body = new StackPanel { Spacing = 2, Margin = new Thickness(4, 2, 4, 2) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        foreach (var command in commands)
        {
            var icon = new VectorIcon(command.Icon, 24) { HorizontalAlignment = HorizontalAlignment.Center };
            _icons.Add(icon);
            var content = new StackPanel { Spacing = 4 };
            content.Children.Add(icon);
            content.Children.Add(new TextBlock { Text = command.Caption, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 11, Height = 28, LineHeight = 13, MaxWidth = 68 });
            var button = new Button { Content = content, Width = 68, Height = 68, MinHeight = 0, Padding = new Thickness(4, 4, 4, 0),
                CornerRadius = new CornerRadius(1), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            AutomationProperties.SetName(button, command.ToolTip ?? command.Caption.Replace('\n', ' '));
            AutomationProperties.SetAutomationId(button, command.Id);
            ToolTipService.SetToolTip(button, command.ToolTip ?? command.Caption.Replace('\n', ' '));
            button.Click += (_, _) => { try { command.Execute(); } catch (Exception error) { CommandFailed?.Invoke(this, error); } };
            _commands.Add(command.Id, button); buttons.Children.Add(button);
        }
        body.Children.Add(buttons);
        body.Children.Add(new TextBlock { Text = caption, FontSize = 10, TextAlignment = TextAlignment.Center, Height = 17, Opacity = .75 });
        var divider = new Border { Child = body, BorderThickness = new Thickness(0, 0, 1, 0), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 216, 216, 216)) };
        _dividers.Add(divider); _groups.Children.Add(divider);
    }
    public void SetTheme(bool dark)
    {
        RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        _border.Background = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(255, 40, 40, 40) : Microsoft.UI.Colors.White);
        var line = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(255, 74, 74, 74) : Windows.UI.Color.FromArgb(255, 216, 216, 216));
        _border.BorderBrush = line;
        foreach (var divider in _dividers) divider.BorderBrush = line;
        foreach (var icon in _icons) icon.IsDark = dark;
    }
}
