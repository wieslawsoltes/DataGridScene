using DataGridScene.Core;
using DataGridScene.Layout;
using DataGridScene.Rendering.Skia;
using SkiaSharp;
using System.Diagnostics;

namespace DataGridScene.Validation;

internal static class RenderReviewValidation
{
    private static void Check(bool ok, string message = "Assertion failed.")
    { if (!ok) throw new InvalidOperationException(message); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    internal static ValidationReport Append(ValidationReport previous)
    {
        var clock = Stopwatch.StartNew(); var results = previous.Results.ToList();
        void Test(string name, Action action)
        {
            var timer = Stopwatch.StartNew();
            try { action(); results.Add(new(name,true,timer.Elapsed.TotalMilliseconds,null)); }
            catch(Exception e) { results.Add(new(name,false,timer.Elapsed.TotalMilliseconds,e.ToString())); }
        }
        Test("render.cache-reuses-value-updates-and-selection", () => {
            using var view = new GridView(ValidationSuite.Fixture());
            view.SetQuery(new() { Summaries=[new("Amount",SummaryKind.Sum)] });
            var layout=new GridLayout(view.Table.Schema); var heights=new RowHeightIndex(view.Rows.Count); var selection=new GridSelection();
            var cache=new GridFrameCache(); var metrics=new GridMetrics();
            GridFrame Frame()=>cache.Get(view,layout,selection,heights,metrics,600,400,0,0);
            var initial=Frame(); var totals=SummaryGeometry.TotalCells(initial);
            for(int i=0;i<20;i++) { view.Table.Apply([new(1,"Name","Changed "+i)]); selection.Select(view,new(1,"Name")); Check(ReferenceEquals(initial,Frame())); }
            Check(cache.BuildCount==1 && cache.ReuseCount==20);
            Check(ReferenceEquals(totals,SummaryGeometry.TotalCells(Frame())) && initial.SummaryGeometryBuildCount==1);
            view.Table.Apply([new(1,"Amount",50m)]);
            var updated=Frame(); Check(!ReferenceEquals(updated,initial));
            Check(SummaryGeometry.TotalCells(updated).Single().Text.Contains("100"));
        });
        Test("render.cache-invalidates-every-geometry-input", () => {
            using var view=new GridView(ValidationSuite.Fixture()); var layout=new GridLayout(view.Table.Schema);
            var heights=new RowHeightIndex(view.Rows.Count); var selection=new GridSelection(); var cache=new GridFrameCache(); var metrics=new GridMetrics();
            double x=0,y=0,width=500,height=170;
            GridFrame Frame()=>cache.Get(view,layout,selection,heights,metrics,width,height,x,y);
            var previousFrame=Frame();
            void Changed() {var current=Frame();Check(!ReferenceEquals(previousFrame,current));previousFrame=current;}
            layout.SetWidth("Name",240);Changed(); heights.SetHeight(0,50);Changed(); x=90;Changed();y=30;Changed();width=520;Changed();height=200;Changed();
            metrics=metrics with {ShowBands=true};Changed();
            view.SetQuery(new() {Sorts=[new("Name")]});Changed();
            var other=new GridSelection();Check(!ReferenceEquals(previousFrame,cache.Get(view,layout,other,heights,metrics,width,height,x,y)));
            cache.Clear();Check(!ReferenceEquals(previousFrame,Frame()));
        });
        Test("render.cached-summary-rectangles-share-pointer-geometry", () => {
            using var view=new GridView(ValidationSuite.Fixture());
            view.SetQuery(new() { Groups=[new("Region")],GroupSummaryDisplay=GroupSummaryDisplayMode.AlignByColumns,
                GroupFooterDisplay=GroupFooterDisplayMode.Always, Summaries=[new("Amount",SummaryKind.Sum){ShowInGroupFooter=true}] });
            var heights=new RowHeightIndex(view.Rows.Count);
            for(int i=0;i<view.Rows.Count;i++) heights.SetHeight(i,SummaryGeometry.RowHeight(view.Query,view.Rows[i].Kind,28));
            var frame=new GridFrame(view,new GridLayout(view.Table.Schema),new GridSelection(),heights,new(),850,900,0,0);
            var first=SummaryGeometry.VisibleCells(frame).ToArray(); long builds=frame.SummaryGeometryBuildCount;
            for(int repeat=0;repeat<10;repeat++) foreach(var cell in SummaryGeometry.VisibleCells(frame)) {
                Check(first.Any(original=>ReferenceEquals(original,cell))); var hit=frame.HitTest(cell.Bounds.X+2,cell.Bounds.Y+10);
                Check(hit.SummaryKey==cell.Key);
            }
            Check(builds==frame.SummaryGeometryBuildCount);
        });
        Test("render.frame-rejects-mismatched-geometry", () => {
            using var view=new GridView(ValidationSuite.Fixture()); var layout=new GridLayout(view.Table.Schema);
            Reject<ArgumentException>(()=>new GridFrame(view,layout,new(),new RowHeightIndex(1),new(),600,400,0,0));
            Reject<ArgumentOutOfRangeException>(()=>new GridFrame(view,layout,new(),new RowHeightIndex(4),new(){BandHeight=0},600,400,0,0));
            Reject<ArgumentOutOfRangeException>(()=>new GridFrame(view,layout,new(),new RowHeightIndex(4),new(),double.NaN,400,0,0));
        });
        foreach(string origin in new[]{"getter","style","detail"}) Test("render.callback-"+origin+"-restores-caller-state", () => {
            bool fail=false;
            var table=new GridTable(new GridSchema([new("Name",typeof(string)),new("Computed",typeof(string)) { ValueGetter=row=>fail&&origin=="getter"?throw new ApplicationException("Expected callback failure"):row[0],ValueDependencies=["Name"] }]));
            table.Add(new(1,new object?[]{"Alpha",null}));using var view=new GridView(table);
            if(origin=="detail")view.ToggleDetail(1);
            var frame=new GridFrame(view,new GridLayout(table.Schema),new(),new RowHeightIndex(view.Rows.Count),new(),300,280,0,0);
            using var renderer=new GridCanvasRenderer();
            renderer.ConditionalFormats.Add(new((_,_)=>fail&&origin=="style"?throw new ApplicationException("Expected callback failure"):false,new()));
            renderer.DetailProvider=_=>fail&&origin=="detail"?throw new ApplicationException("Expected callback failure"):new GridDetailData(["Name"],[new[]{"Detail"}]);
            using var surface=SKSurface.Create(new SKImageInfo(400,400));var canvas=surface.Canvas;
            canvas.Save();canvas.Translate(7,11);canvas.Scale(1.1f);canvas.ClipRect(new SKRect(0,0,310,305));
            int count=canvas.SaveCount;var matrix=canvas.TotalMatrix;var clip=canvas.LocalClipBounds;
            fail=true;Reject<ApplicationException>(()=>renderer.Draw(canvas,frame));
            Check(count==canvas.SaveCount,"Renderer leaked nested save/clip levels");Check(matrix==canvas.TotalMatrix && clip==canvas.LocalClipBounds,"Caller transform or clip changed");
            fail=false;canvas.Clear(SKColors.Transparent);renderer.Draw(canvas,frame);
            using var actual=surface.Snapshot();using var bytes=actual.Encode();
            canvas.Clear(SKColors.Transparent);renderer.Draw(canvas,frame);using var expected=surface.Snapshot();using var expectedBytes=expected.Encode();
            Check(bytes.ToArray().SequenceEqual(expectedBytes.ToArray()));Check(canvas.SaveCount==count);
        });
        Test("render.cached-and-fresh-raster-match", () => {
            using var view=new GridView(ValidationSuite.Fixture());var layout=new GridLayout(view.Table.Schema);var heights=new RowHeightIndex(4);var selection=new GridSelection();
            var metrics=new GridMetrics {ShowBands=true}; var cache=new GridFrameCache();using var renderer=new GridCanvasRenderer();
            using var surface=SKSurface.Create(new SKImageInfo(720,420));
            for(int i=0;i<4;i++) {
                view.Table.Apply([new(1,"Name","Changed "+i)]);selection.Select(view,new(i+1,"Name"));
                renderer.Draw(surface.Canvas,cache.Get(view,layout,selection,heights,metrics,720,420,0,0));using var a=surface.Snapshot();using var aBytes=a.Encode();
                renderer.Draw(surface.Canvas,new GridFrame(view,layout,selection,heights,metrics,720,420,0,0));using var b=surface.Snapshot();using var bBytes=b.Encode();
                Check(aBytes.ToArray().SequenceEqual(bBytes.ToArray()));
            }
            Check(cache.BuildCount==1);
        });
        Test("render.disposal-is-idempotent-and-guarded", () => {
            using var view=new GridView(ValidationSuite.Fixture());using var surface=SKSurface.Create(new SKImageInfo(400,400));
            var frame=new GridFrame(view,new GridLayout(view.Table.Schema),new(),new RowHeightIndex(4),new(),400,400,0,0);
            var renderer=new GridCanvasRenderer();renderer.Dispose();renderer.Dispose();
            Reject<ObjectDisposedException>(()=>renderer.Draw(surface.Canvas,frame));
        });
        return new(results.AsReadOnly(),previous.Milliseconds+clock.Elapsed.TotalMilliseconds);
    }
}
