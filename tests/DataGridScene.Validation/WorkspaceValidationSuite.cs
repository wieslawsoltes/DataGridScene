using DataGridScene.Core;
using DataGridScene.Layout;
using System.Diagnostics;
using System.Text.Json;

namespace DataGridScene.Validation;

/// <summary>Band geometry, indexed virtualization and complete workspace snapshot contracts.</summary>
public static class WorkspaceValidationSuite
{
    private static void Check(bool value, string message = "Assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }
    private static void Near(double expected, double actual)
    { Check(Math.Abs(expected - actual) < 0.00001, $"Expected {expected}, received {actual}."); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static GridLayout Layout()
    {
        var schema = new GridSchema([
            new("A", typeof(string)) { Band = "Customer", Width = 100 },
            new("B", typeof(string)) { Band = "Customer", Width = 120 },
            new("C", typeof(string)) { Band = "Order", Width = 80 },
            new("D", typeof(string)) { Band = "Order", Width = 90 },
            new("E", typeof(string)) { Band = "Other", Width = 110 }
        ]);
        return new(schema);
    }
    public static async Task<ValidationReport> RunAsync(bool stress = true)
    {
        var watch = Stopwatch.StartNew();
        var baseline = await ExtendedValidationSuite.RunAsync(stress);
        var results = baseline.Results.ToList();
        void Test(string name, Action action)
        {
            var timer = Stopwatch.StartNew();
            try { action(); results.Add(new(name, true, timer.Elapsed.TotalMilliseconds, null)); }
            catch (Exception error) { results.Add(new(name, false, timer.Elapsed.TotalMilliseconds, error.ToString())); }
        }
        Test("bands.adjacent-caption-merged-once", () =>
        {
            var layout = Layout(); var bands = layout.ComputeBands(700, layout.Compute(700, 0));
            Check(bands.Count == 3); Near(220, bands.Single(band => band.Key == "/Customer").Width);
            Near(38, bands[0].X); Check(bands[0].RowSpan == 1);
        });
        Test("bands.nested-header-rows", () =>
        {
            var layout = Layout(); layout.SetBandPath("A", "Customer", "Identity"); layout.SetBandPath("B", "Customer", "Contact");
            var bands = layout.ComputeBands(700, layout.Compute(700, 0));
            Check(layout.BandLevels == 2);
            Check(bands.Count == 5 && bands.Single(band => band.Key == "/Order").RowSpan == 2);
            Check(bands.Single(band => band.Key == "/Customer").RowSpan == 1);
            Check(bands.Single(band => band.Key == "/Customer/Identity").Depth == 1);
        });
        Test("bands.path-is-owned-and-readonly", () =>
        {
            var layout = Layout(); string[] path = ["Customer", "Contact"]; layout.SetBandPath("A", path); path[0] = "Changed";
            Check(layout.Columns[0].BandPath![0] == "Customer");
            Reject<NotSupportedException>(() => ((IList<string>)layout.Columns[0].BandPath!)[0] = "Changed");
            Reject<NotSupportedException>(() => ((IList<ColumnState>)layout.Columns)[0] = layout.Columns[1]);
        });
        Test("bands.escaped-caption-identities", () =>
        {
            var layout = Layout(); layout.SetBandPath("A", "A/B", "C"); layout.SetBandPath("B", "A", "B/C");
            Check(GridLayout.BandKey(["A/B", "C"]) != GridLayout.BandKey(["A", "B/C"]));
            Check(layout.FieldsInBand("/A%2FB").SequenceEqual(new[] { "A" }));
        });
        Test("bands.disjoint-runs-do-not-cover-other-bands", () =>
        {
            var layout = Layout(); layout.MoveBefore("C", "B");
            var bands = layout.ComputeBands(700, layout.Compute(700, 0));
            Check(bands.Count(band => band.Key == "/Customer") == 2);
            Near(100, bands.First(band => band.Key == "/Customer").Width);
        });
        Test("bands.hidden-column-no-spurious-gap", () =>
        {
            var layout = Layout(); layout.SetVisible("A", false);
            var band = layout.ComputeBands(700, layout.Compute(700, 0)).Single(band => band.Key == "/Customer");
            Near(120, band.Width); Near(38, band.X); Check(band.FirstField == "B");
        });
        Test("bands.pinned-panes-are-separate", () =>
        {
            var layout = Layout(); layout.SetPin("A", ColumnPin.Left); layout.SetPin("B", ColumnPin.Right);
            var viewport = layout.Compute(500, 40); var bands = layout.ComputeBands(500, viewport);
            Check(bands.Count(band => band.Key == "/Customer") == 2);
            Near(38, bands.Single(band => band.Pin == ColumnPin.Left).X);
            Near(380, bands.Single(band => band.Pin == ColumnPin.Right).X);
            Check(bands.All(band => band.ClipLeft >= 38 && band.ClipRight <= 500));
        });
        Test("bands.pin-entire-hierarchy-one-notification", () =>
        {
            var layout = Layout(); layout.SetBandPath("A", "Customer", "Identity");
            int notifications = 0; layout.Changed += (_, _) => notifications++;
            layout.SetBandPin("/Customer", ColumnPin.Right);
            Check(notifications == 1 && layout.Columns.Where(column => column.Field is "A" or "B").All(column => column.Pin == ColumnPin.Right));
        });
        Test("bands.hide-show-entire-band", () =>
        {
            var layout = Layout(); layout.SetBandVisible("/Customer", false);
            Check(layout.DisplayColumns().Count == 3);
            layout.SetBandVisible("/Customer", true); Check(layout.DisplayColumns().Count == 5);
        });
        Test("bands.cannot-hide-last-visible-band", () =>
        {
            var layout = Layout(); layout.SetBandVisible("/Customer", false); layout.SetBandVisible("/Order", false);
            int revision = layout.Revision;
            Reject<InvalidOperationException>(() => layout.SetBandVisible("/Other", false));
            Check(layout.Revision == revision && layout.DisplayColumns().Count == 1);
        });
        Test("bands.move-preserves-child-order", () =>
        {
            var layout = Layout(); layout.MoveBandBefore("/Order", "/Customer");
            Check(string.Join(",", layout.DisplayColumns().Select(column => column.Field)) == "C,D,A,B,E");
        });
        Test("bands.reject-cross-parent-reorder", () =>
        {
            var layout = Layout(); layout.SetBandPath("A", "Customer", "Identity");
            Reject<InvalidOperationException>(() => layout.MoveBandBefore("/Customer/Identity", "/Order"));
        });
        Test("bands.proportional-resize-atomic", () =>
        {
            var layout = Layout(); int notifications = 0; layout.Changed += (_, _) => notifications++;
            layout.SetBandWidth("/Customer", 440);
            Near(200, layout.Columns[0].Width); Near(240, layout.Columns[1].Width); Near(80, layout.Columns[2].Width);
            Check(notifications == 1);
        });
        Test("bands.resize-clamps-individual-widths", () =>
        {
            var layout = Layout(); layout.SetWidth("A", 36); layout.SetWidth("B", 400);
            layout.SetBandWidth("/Customer", 100);
            Near(36, layout.Columns[0].Width); Near(64, layout.Columns[1].Width);
        });
        Test("bands.resize-restores-original-proportions", () =>
        {
            var layout = Layout(); var original = layout.Columns.ToDictionary(column => column.Field, column => column.Width);
            layout.SetBandWidth("/Customer", 72, original);
            layout.SetBandWidth("/Customer", 220, original);
            Near(100, layout.Columns[0].Width); Near(120, layout.Columns[1].Width);
        });
        Test("bands.invalid-resize-is-atomic", () =>
        {
            var layout = Layout(); int revision = layout.Revision;
            Reject<ArgumentOutOfRangeException>(() => layout.SetBandWidth("/Customer", 20));
            Reject<ArgumentOutOfRangeException>(() => layout.SetBandWidth("/Customer", double.NaN));
            Reject<ArgumentException>(() => layout.SetBandWidth("/Customer", 200, new Dictionary<string, double> { ["A"] = 0 }));
            Check(revision == layout.Revision);
        });
        Test("bands.path-validation", () =>
        {
            var layout = Layout();
            Reject<ArgumentException>(() => layout.SetBandPath("A"));
            Reject<ArgumentException>(() => layout.SetBandPath("A", " "));
            Reject<ArgumentException>(() => layout.SetBandPath("A", Enumerable.Repeat("X", 17).ToArray()));
            Reject<ArgumentException>(() => layout.SetBandPath("A", new string('x', 257)));
            Reject<ArgumentException>(() => layout.SetBandPin("/Missing", ColumnPin.Left));
        });
        Test("columns.prefix-index-exact-boundary", () =>
        {
            var layout = Layout(); var viewport = layout.Compute(250, 100);
            Check(viewport.Slots[0].Field == "B"); Near(38, viewport.Slots[0].X);
        });
        Test("columns.viewport-too-narrow-for-fixed-columns", () =>
        {
            var layout = Layout(); layout.SetBandPin("/Customer", ColumnPin.Left); layout.SetBandPin("/Order", ColumnPin.Right);
            var viewport = layout.Compute(100, 50);
            Check(viewport.Slots.All(slot => slot.ClipRight <= 100 && slot.ClipLeft >= 38));
            Check(viewport.Slots.Count == 1 && viewport.Slots[0].Field == "A");
        });
        Test("columns.prefix-index-randomized-differential", () =>
        {
            var random = new Random(72819);
            var layout = Layout();
            for (int iteration = 0; iteration < 200; iteration++)
            {
                foreach (var column in layout.Columns.ToArray())
                {
                    layout.SetWidth(column.Field, random.Next(36, 300));
                    layout.SetPin(column.Field, (ColumnPin)random.Next(3));
                }
                double width = random.Next(100, 1000), offset = random.Next(1000);
                var actual = layout.Compute(width, offset);
                var states = layout.DisplayColumns();
                double leftTotal = states.Where(c => c.Pin == ColumnPin.Left).Sum(c => c.Width);
                double rightTotal = states.Where(c => c.Pin == ColumnPin.Right).Sum(c => c.Width);
                var expected = new Dictionary<string, (double X, double L, double R)>();
                foreach (var pin in new[] { ColumnPin.None, ColumnPin.Left, ColumnPin.Right })
                {
                    double x = pin == ColumnPin.Left ? 38 : pin == ColumnPin.Right ? width - rightTotal : actual.MiddleLeft - actual.Offset;
                    double clipLeft = pin == ColumnPin.Left ? Math.Min(38, width) : pin == ColumnPin.Right ? actual.MiddleRight : actual.MiddleLeft;
                    double clipRight = pin == ColumnPin.Left ? actual.MiddleLeft : pin == ColumnPin.Right ? width : actual.MiddleRight;
                    foreach (var column in states.Where(c => c.Pin == pin))
                    {
                        double l = Math.Max(x, clipLeft), r = Math.Min(x + column.Width, clipRight);
                        if (r > l) expected.Add(column.Field, (x, l, r));
                        x += column.Width;
                    }
                }
                Check(actual.Slots.Count == expected.Count);
                foreach (var slot in actual.Slots)
                {
                    var e = expected[slot.Field]; Near(e.X, slot.X); Near(e.L, slot.ClipLeft); Near(e.R, slot.ClipRight);
                }
            }
        });
        Test("state.expansion-roundtrip-once", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture());
            view.SetQuery(new() { Groups = [new("Region")] });
            var west = view.Groups.Single(group => Equals(group.Value, "West"));
            view.ToggleDetail(2); view.ToggleGroup(west.Key);
            var state = view.CaptureState();
            view.ExpandAll(); view.ToggleDetail(2);
            int notifications = 0; view.Changed += (_, _) => notifications++;
            view.RestoreState(state);
            Check(notifications == 1 && !view.IsExpanded(west.Key) && view.IsDetailExpanded(2));
        });
        Test("state.expansion-skips-deleted-and-missing-groups", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture());
            view.RestoreState(new(new[] { "/Missing" }, new long[] { 1, 999 }));
            Check(view.CaptureState().CollapsedGroups.Count == 0 && view.CaptureState().ExpandedDetails.SequenceEqual(new long[] { 1 }));
        });
        Test("state.invalid-expansion-atomic", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture()); view.ToggleDetail(1); int revision = view.Revision;
            Reject<ArgumentException>(() => view.RestoreState(new(new[] { "" }, Array.Empty<long>())));
            Check(view.Revision == revision && view.IsDetailExpanded(1));
        });
        Test("state.cell-selection-compressed-roundtrip", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture());
            var selection = new GridSelection { Mode = GridSelectionMode.Cells };
            selection.Select(view, new(1, "Name")); selection.Select(view, new(3, "Amount"), extend: true);
            var state = selection.CaptureState();
            Check(state.CellBlocks.Count == 1 && state.CellBlocks[0].RecordIds.Count == 3);
            selection.Clear(); selection.RestoreState(view, state);
            Check(selection.SelectedCellCount == 9 && selection.FocusedCell == state.FocusedCell && selection.Anchor == state.Anchor);
        });
        Test("state.sparse-selection-and-hidden-focus", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture());
            var selection = new GridSelection { Mode = GridSelectionMode.Cells };
            selection.Select(view, new(1, "Name")); selection.Select(view, new(3, "Amount"), toggle: true);
            var state = selection.CaptureState();
            view.SetQuery(new() { Search = "Beta" }); selection.RestoreState(view, state);
            Check(selection.Count == 2 && selection.SelectedCellCount == 2 && selection.FocusedCell!.Value.RecordId == 3);
        });
        Test("state.selection-schema-and-data-evolution", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture());
            var selection = new GridSelection();
            selection.RestoreState(view, new(GridSelectionMode.Cells, Array.Empty<long>(),
                new[] { new GridCellSelectionBlock(new long[] { 1, 999 }, new[] { "Name", "Removed" }) }, new(999, "Name")));
            Check(selection.SelectedCellCount == 1 && selection.ContainsCell(1, "Name") && selection.FocusedCell is null);
        });
        Test("state.selection-invalid-mode-atomic", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture());
            var selection = new GridSelection(); selection.Select(view, new(1, "Name"));
            Reject<ArgumentException>(() => selection.RestoreState(view, new(GridSelectionMode.SingleRow, new long[] { 1, 2 }, Array.Empty<GridCellSelectionBlock>())));
            Check(selection.Count == 1 && selection.Contains(1));
        });
        Test("state.restore-notification-one", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture());
            var selection = new GridSelection(); selection.SelectAll(view); var state = selection.CaptureState();
            int events = 0; selection.Changed += (_, _) => events++;
            selection.RestoreState(view, state); Check(events == 1 && selection.Count == 4);
        });
        Test("layout.v2-workspace-roundtrip", () =>
        {
            var table = ValidationSuite.Fixture(); using var view = new GridView(table);
            view.SetQuery(new() { Groups = [new("Region")] }); view.ToggleDetail(2);
            var selection = new GridSelection { Mode = GridSelectionMode.Cells }; selection.Select(view, new(2, "Name"));
            var layout = new GridLayout(table.Schema); layout.SetBandPath("Name", "Customer", "Contact");
            var document = new GridLayoutDocument
            {
                Columns = layout.Columns, Query = view.Query, SelectionMode = selection.Mode, Selection = selection.CaptureState(), ViewState = view.CaptureState(),
                ShowBands = true, HorizontalOffset = 22, VerticalOffset = 60,
                FilterTexts = new Dictionary<string, string> { ["Name"] = "Beta" },
                RowHeights = new Dictionary<string, double> { ["row:2"] = 52 }
            };
            var restored = LayoutSerializer.Load(LayoutSerializer.Save(document), table.Schema);
            Check(restored.Version == 2 && restored.ShowBands && restored.Selection!.CellBlocks.Count == 1 && restored.ViewState.ExpandedDetails.Count == 1);
            Check(restored.Columns.Single(column => column.Field == "Name").BandPath!.Count == 2);
            Near(22, restored.HorizontalOffset); Near(60, restored.VerticalOffset); Near(52, restored.RowHeights["row:2"]);
            view.SetQuery(restored.EffectiveQuery()); Check(view.FilteredRecords.Count == 1 && view.FilteredRecords[0].Id == 2);
        });
        Test("layout.filter-row-remains-independently-editable", () =>
        {
            var table = ValidationSuite.Fixture(); using var view = new GridView(table);
            var document = new GridLayoutDocument { Columns = new GridLayout(table.Schema).Columns,
                Query = new() { Filter = new FieldFilter("Amount", FilterOperator.Greater, 5) },
                FilterTexts = new Dictionary<string, string> { ["Name"] = "Alpha" } };
            var restored = LayoutSerializer.Load(LayoutSerializer.Save(document), table.Schema);
            view.SetQuery(restored.EffectiveQuery()); Check(view.FilteredRecords.Single().Id == 1);
            restored = restored with { FilterTexts = new Dictionary<string, string> { ["Name"] = "Beta" } };
            view.SetQuery(restored.EffectiveQuery()); Check(view.FilteredRecords.Single().Id == 2);
        });
        Test("layout.v1-migration", () =>
        {
            var table = ValidationSuite.Fixture();
            var document = LayoutSerializer.Load("""{"version":1,"columns":[{"field":"name","width":220,"order":0,"visible":true,"pin":"Left"}],"query":{"search":"Alpha"}}""", table.Schema);
            Check(document.Columns[0].Field == "Name" && document.Columns[0].BandPath!.Count == 1 && !document.ShowBands);
            using var view = new GridView(table); view.SetQuery(document.EffectiveQuery()); Check(view.FilteredRecords.Single().Id == 1);
        });
        foreach (var (name, json) in new[]
        {
            ("negative-offset", """{"verticalOffset":-1}"""), ("null-query-lists", """{"query":{"sorts":null}}"""),
            ("null-filter-row", """{"filterTexts":null}"""), ("invalid-row-height", """{"rowHeights":{"row:1":0}}"""),
            ("unknown-filter-field", """{"filterTexts":{"Missing":"x"}}"""),
            ("duplicate-filter-field", """{"filterTexts":{"Name":"x","name":"y"}}"""),
            ("invalid-selection-mode", """{"selectionMode":"Invalid"}"""),
            ("invalid-band-path", """{"columns":[{"field":"Name","width":100,"order":0,"bandPath":[""]}]}"""),
            ("null-expansion", """{"viewState":null}"""),
            ("null-field-filter", """{"query":{"filter":{"$kind":"group","all":true,"children":[null]}}}""")
        })
        {
            Test("layout.reject-" + name, () =>
            {
                try { _ = LayoutSerializer.Load(json, ValidationSuite.Fixture().Schema); }
                catch (Exception error) when (error is FormatException or ArgumentException) { return; }
                throw new InvalidOperationException("Malformed layout was accepted: " + json);
            });
        }
        if (stress)
        {
            Test("stress.band-and-column-index-10000-columns", () =>
            {
                var schema = new GridSchema(Enumerable.Range(0, 10000).Select(i => new GridColumn("C" + i, typeof(int)) { Width = 100, Band = "Band" + i / 10 }));
                var layout = new GridLayout(schema); var viewport = layout.Compute(1000, 500000);
                var bands = layout.ComputeBands(1000, viewport);
                Check(viewport.Slots.Count <= 11 && bands.Count <= 2 && viewport.Slots[0].SchemaIndex == 5000);
            });
            Test("stress.select-all-snapshot-size", () =>
            {
                var table = new GridTable(new GridSchema([new("A", typeof(int)), new("B", typeof(int))]));
                table.AddRange(Enumerable.Range(0, 100000).Select(i => new GridRecord(i, new object?[] { i, i })));
                using var view = new GridView(table); var selection = new GridSelection { Mode = GridSelectionMode.Cells }; selection.SelectAll(view);
                var state = selection.CaptureState();
                Check(state.CellBlocks.Count == 1 && JsonSerializer.Serialize(state).Length < 1000000);
                var restored = new GridSelection(); restored.RestoreState(view, state);
                Check(restored.SelectedCellCount == 200000);
            });
        }
        return new(results.AsReadOnly(), watch.Elapsed.TotalMilliseconds);
    }
}
