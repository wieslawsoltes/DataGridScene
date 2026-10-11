using DataGridScene.Core;
using Microsoft.UI.Xaml;

namespace DataGridScene.Controls;

/// <summary>Reusable native cell content. Create must return a fresh unparented element;
/// Bind may be called repeatedly, Unbind must remove cell-specific subscriptions, and
/// Release is called when an element leaves the bounded pool. Application callbacks are
/// trusted code. Use context.Value and context.TryCommit(), never capture a recycled row.</summary>
public sealed class GridCellTemplate
{
    public Func<FrameworkElement> Create { get; }
    public Action<FrameworkElement, GridCellEditBuffer>? Bind { get; init; }
    /// <summary>Read pending internal input (e.g. numeric text) before committing. Do not mutate the table directly.</summary>
    public Action<FrameworkElement, GridCellEditBuffer>? Flush { get; init; }
    public Action<FrameworkElement, GridCellEditBuffer>? Unbind { get; init; }
    public Action<FrameworkElement>? Release { get; init; }
    public bool CommitOnLostFocus { get; init; } = true;
    public GridCellTemplate(Func<FrameworkElement> create)
    { ArgumentNullException.ThrowIfNull(create); Create = create; }
    public static GridCellTemplate FromDataTemplate(DataTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return new(() => template.LoadContent() as FrameworkElement ??
            throw new InvalidOperationException("A cell DataTemplate must create a FrameworkElement."));
    }
}
