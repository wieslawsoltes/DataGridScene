using DataGridScene.Core;
using DataGridScene.Rendering.Skia;
using DataGridScene.Layout;
using SkiaSharp;
using System.Diagnostics;

namespace DataGridScene.Validation;

public static class NativeCellValidationSuite
{
    private static void Check(bool ok, string message = "Native cell contract failed.") { if (!ok) throw new InvalidOperationException(message); }
    private static void Reject<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    public static async Task<ValidationReport> RunAsync(bool stress = true)
    {
        var previous = await FilterPresentationValidationSuite.RunAsync(stress);
        var timer = Stopwatch.StartNew(); var results = previous.Results.ToList();
        void Test(string name, Action action) {
            var started = Stopwatch.GetTimestamp();
            try { action(); results.Add(new(name, true, Stopwatch.GetElapsedTime(started).TotalMilliseconds, null)); }
            catch(Exception error) { results.Add(new(name, false, Stopwatch.GetElapsedTime(started).TotalMilliseconds, error.ToString())); }
        }
        Test("native-buffer.untouched-value-does-not-create-history", () => {
            var table = ValidationSuite.Fixture(); var history = new EditHistory(table);
            using var buffer = new GridCellEditBuffer(table, history, 1, "name"); int version = table[1].Version;
            Check(buffer.Field == "Name" && Equals(buffer.Value, "Alpha") && !buffer.IsDirty && buffer.CanEdit);
            Check(buffer.TryCommit() && table[1].Version == version && !history.CanUndo);
        });
        Test("native-buffer.commit-validation-and-undo", () => {
            var table = ValidationSuite.Fixture(); var history = new EditHistory(table);
            using var buffer = new GridCellEditBuffer(table, history, 1, "Amount");
            buffer.Value = "42.5"; Check(buffer.IsDirty && buffer.TryCommit() && !buffer.IsDirty);
            Check(Equals(table.Schema["Amount"].GetValue(table[1]), 42.5m));
            Check(history.Undo()); buffer.Refresh(); Check(Equals(buffer.Value, 10m));
            Check(history.Redo()); buffer.Refresh(); Check(Equals(buffer.Value, 42.5m));
        });
        Test("native-buffer.invalid-input-retained-until-cancel", () => {
            var table = ValidationSuite.Fixture(); var history = new EditHistory(table);
            using var buffer = new GridCellEditBuffer(table, history, 1, "Amount"); int version = table[1].Version;
            buffer.Value = "not a number"; Check(!buffer.TryCommit() && buffer.Error is not null && buffer.IsDirty && table[1].Version == version);
            buffer.Refresh(); Check(Equals(buffer.Value, "not a number")); buffer.Cancel();
            Check(!buffer.IsDirty && buffer.Error is null && Equals(buffer.Value, 10m));
        });
        Test("native-buffer.readonly-transition-and-recovery", () => {
            var table = ValidationSuite.Fixture(); bool canEdit = true;
            using var buffer = new GridCellEditBuffer(table, new(table), 1, "Name", () => canEdit);
            buffer.Value = "Changed"; canEdit = false; Check(!buffer.TryCommit() && !buffer.CanEdit);
            canEdit = true; Check(buffer.TryCommit() && Equals(buffer.Value, "Changed"));
        });
        Test("native-buffer.external-write-conflicts-with-dirty-cell", () => {
            var table = ValidationSuite.Fixture(); using var buffer = new GridCellEditBuffer(table, new(table), 1, "Name");
            buffer.Value = "Pending"; table.Apply([new(1,"Name","External")]); buffer.Refresh();
            Check(Equals(buffer.Value,"Pending") && !buffer.TryCommit()); buffer.Cancel(); Check(Equals(buffer.Value,"External"));
        });
        Test("native-buffer.pristine-refresh-tracks-record-version", () => {
            var table = ValidationSuite.Fixture(); using var buffer = new GridCellEditBuffer(table, new(table), 1, "Name");
            table.Apply([new(1,"Name","External")]); buffer.Refresh(); Check(Equals(buffer.Value,"External"));
            buffer.Value="Next"; Check(buffer.TryCommit());
        });
        Test("native-buffer.replacement-identity-cannot-receive-old-edit", () => {
            var table = ValidationSuite.Fixture(); using var buffer = new GridCellEditBuffer(table, new(table), 1, "Name");
            buffer.Value="Old pending"; table.Remove([1]); var replacement = ValidationSuite.Fixture()[1]; table.Add(replacement);
            Check(!buffer.TryCommit() && buffer.Error!.Contains("replaced"));
            Check(Equals(table.Schema["Name"].GetValue(replacement),"Alpha"));
        });
        Test("native-buffer.disposal-suppresses-late-events", () => {
            var table=ValidationSuite.Fixture(); var buffer=new GridCellEditBuffer(table,new(table),1,"Name");int notifications=0;
            buffer.PropertyChanged+=(_,_)=>notifications++;buffer.Dispose();buffer.Dispose();buffer.Value="Late";
            Check(!buffer.IsActive && !buffer.TryCommit() && notifications==0 && Equals(table.Schema["Name"].GetValue(table[1]),"Alpha"));
        });
        Test("native-buffer.binding-synchronization-never-writes", () => {
            var table=ValidationSuite.Fixture();using var buffer=new GridCellEditBuffer(table,new(table),1,"Name");int version=table[1].Version;
            buffer.SynchronizeBinding(()=>{buffer.Value="Default from native control";Check(buffer.TryCommit());});
            Check(!buffer.IsDirty && Equals(buffer.Value,"Alpha") && table[1].Version==version);
            Reject<InvalidOperationException>(()=>buffer.SynchronizeBinding(()=>throw new InvalidOperationException()));
            buffer.Value="Real edit";Check(buffer.TryCommit());
        });
        Test("native-buffer.history-owner-is-validated",()=>{
            var table=ValidationSuite.Fixture(); Reject<ArgumentException>(()=>new GridCellEditBuffer(table,new(ValidationSuite.Fixture()),1,"Name"));
        });
        Test("native-buffer.actions-capture-current-identity-and-errors",()=>{
            var table=ValidationSuite.Fixture();using var buffer=new GridCellEditBuffer(table,new(table),1,"Name");
            Check(!buffer.Invoke(_=>throw new InvalidOperationException("Rejected action"))&&buffer.Error=="Rejected action");
            Check(buffer.Invoke(b=>Check(b.RecordId==1 && ReferenceEquals(b.Record,table[1])))&&buffer.Error is null);
        });
        Test("native-buffer.null-versus-empty-and-timestamp-precision",()=>{
            var table=new GridTable(new GridSchema([new("Text",typeof(string)),new("Date",typeof(DateTime))]));
            var date=new DateTime(2026,10,11,12,34,56,DateTimeKind.Utc).AddTicks(1234567);table.Add(new(1,new object?[]{null,date}));
            using var text=new GridCellEditBuffer(table,new(table),1,"Text");Check(text.Value is null && text.TryCommit());
            using var time=new GridCellEditBuffer(table,new(table),1,"Date");Check(time.TryCommit()&&Equals(time.Value,date));
            text.Value="";Check(text.IsDirty&&text.TryCommit()&&Equals(text.Value,""));
        });
        Test("native-geometry.only-visible-requested-data-cells",()=>{
            using var view=new GridView(ValidationSuite.Fixture(),new(){Groups=[new("Region")]});view.ToggleDetail(1);
            var heights=new RowHeightIndex(view.Rows.Count); var layout=new GridLayout(view.Table.Schema);
            var frame=new GridFrame(view,layout,new(),heights,new(),550,270,40,35);
            var cells=NativeCellGeometry.Collect(frame,new HashSet<string>{"Name","Amount"});
            Check(cells.Count>0);foreach(var cell in cells) {
                Check(cell.Address.Field is "Name" or "Amount");Check(cell.ClipBounds.Y>=frame.BodyTop&&cell.ClipBounds.Bottom<=frame.FooterTop);
                Check(cell.ClipBounds.Width>0&&cell.ClipBounds.Height>0&&cell.Bounds.Width>=cell.ClipBounds.Width);
                Check(view.Rows[view.IndexOfRecord(cell.Address.RecordId)].Kind==GridRowKind.Data);
            }
            Reject<InvalidOperationException>(()=>NativeCellGeometry.Collect(frame,new HashSet<string>{"Name","Amount"},1));
        });
        Test("native-geometry.full-width-survives-frozen-pane-clipping",()=>{
            using var view=new GridView(ValidationSuite.Fixture()); var layout=new GridLayout(view.Table.Schema);
            layout.SetPin("Code",ColumnPin.Left);layout.SetWidth("Name",300);
            var frame=new GridFrame(view,layout,new(),new RowHeightIndex(4),new(),420,240,100,10);
            var cells=NativeCellGeometry.Collect(frame,new HashSet<string>{"Name"});
            Check(cells.Count>0&&cells.All(c=>c.Bounds.Width==300&&c.ClipBounds.Width<300));
            foreach(var cell in cells)Check(frame.HitTest(cell.ClipBounds.X+2,cell.ClipBounds.Y+2).Field=="Name");
        });
        Test("native-renderer.skips-hosted-getters-but-retains-grid-lines",()=>{
            int calls=0;var table=new GridTable(new GridSchema([new("Value",typeof(int)){ValueGetter=_=>{calls++;throw new InvalidOperationException();}}]));
            table.Add(new(1,new object?[]{null}));using var view=new GridView(table);var frame=new GridFrame(view,new(table.Schema),new(),new RowHeightIndex(1),new(),300,220,0,0);
            using var renderer=new GridCanvasRenderer{IsNativeCell=_=>true};using var surface=SKSurface.Create(new SKImageInfo(300,220));
            renderer.Draw(surface.Canvas,frame);Check(calls==0&&renderer.CellsDrawn==1);
        });
        if(stress)Test("native-geometry.100000-rows-does-not-realize-offscreen-cells",()=>{
            var table=new GridTable(new GridSchema([new("Value",typeof(int))]));table.AddRange(Enumerable.Range(0,100000).Select(i=>new GridRecord(i,new object?[]{i})));
            using var view=new GridView(table);var frame=new GridFrame(view,new(table.Schema),new(),new RowHeightIndex(100000),new(),500,600,0,1400000);
            var cells=NativeCellGeometry.Collect(frame,new HashSet<string>{"Value"});Check(cells.Count is >0 and <25);
        });
        return new(results.AsReadOnly(), previous.Milliseconds+timer.Elapsed.TotalMilliseconds);
    }
}
