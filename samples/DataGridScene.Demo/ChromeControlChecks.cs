using DataGridScene.Controls;
using DataGridScene.Core;
using DataGridScene.Validation;
using Microsoft.UI.Xaml;
using System.Diagnostics;

namespace DataGridScene.Demo;

internal static class ChromeControlChecks
{
    internal static ValidationReport Run()
    {
        var results=new List<ValidationResult>();var total=Stopwatch.StartNew();
        void Check(bool value) { if(!value)throw new InvalidOperationException("Chrome contract failed."); }
        void Test(string name,Action action) {
            var timer=Stopwatch.StartNew();
            try {action();results.Add(new(name,true,timer.Elapsed.TotalMilliseconds,null));}
            catch(Exception e){results.Add(new(name,false,timer.Elapsed.TotalMilliseconds,e.ToString()));}
        }
        Test("chrome.filter-panel-suspends-and-retains-typed-values",()=>{
            using var grid=new GridControl {Table=ValidationSuite.Fixture()};
            grid.SetColumnValues("Name",new(new object?[]{"Alpha"}));Check(grid.View!.Rows.Count==1&&grid.FilterPanel.Items.Count==1);
            grid.IsFilterEnabled=false;Check(grid.View.Rows.Count==4&&grid.ValueFilters.Count==1&&!grid.FilterPanel.IsFilterEnabled);
            grid.IsFilterEnabled=true;Check(grid.View.Rows.Count==1&&grid.FilterPanel.IsFilterEnabled);
        });
        Test("chrome.panel-does-not-rebuild-on-selection-or-value-edits",()=>{
            using var grid=new GridControl {Table=ValidationSuite.Fixture()};grid.SetColumnFilter("Name","Alpha");
            long count=grid.FilterPanel.ContentBuildCount;
            for(int i=0;i<50;i++){grid.Table!.Apply([new(1,"Amount",i)]);grid.Selection.Select(grid.View!,new(1,"Name"));}
            Check(grid.FilterPanel.ContentBuildCount==count);grid.IsFilterEnabled=false;Check(grid.FilterPanel.ContentBuildCount==count);
        });
        Test("chrome.token-removes-only-one-auto-filter",()=>{
            using var grid=new GridControl {Table=ValidationSuite.Fixture()};grid.SetColumnFilter("Region","West");grid.SetColumnValues("Name",new(new object?[]{"Alpha"}));
            grid.RemoveFilterToken("column:Name");Check(grid.View!.Rows.Count==2&&grid.FilterTexts["Region"]=="West"&&grid.ValueFilters.Count==0);
        });
        Test("chrome.criteria-removal-preserves-checked-filter",()=>{
            using var grid=new GridControl {Table=ValidationSuite.Fixture()};grid.SetCriteria("[Amount] > 15");grid.SetColumnValues("Name",new(new object?[]{"Alpha"}));
            Check(grid.View!.Rows.Count==0);grid.RemoveFilterToken("criteria");Check(grid.View.Rows.Count==1&&grid.ValueFilters.Count==1);
        });
        Test("chrome.clear-panel-preserves-search",()=>{
            using var grid=new GridControl {Table=ValidationSuite.Fixture()};grid.SetSearch("Beta");grid.SetColumnFilter("Name","Alpha");
            grid.ClearFilterConditions();Check(grid.View!.Rows.Count==1&&grid.View.FilteredRecords[0].Id==2&&grid.View.Query.Search=="Beta");
            Check(grid.FilterPanel.Visibility==Visibility.Collapsed&&grid.FilterPanel.Items.Count==0);
        });
        Test("chrome.reserved-looking-column-name-is-not-base-criteria",()=>{
            var table=new GridTable(new GridSchema([new("criteria",typeof(int))]));table.Add(new(1,new object?[]{1}));table.Add(new(2,new object?[]{2}));
            using var grid=new GridControl{Table=table};grid.SetCriteria("[criteria] > 0");grid.SetColumnFilter("criteria","=1");
            Check(grid.FilterPanel.Items.Count==2);grid.RemoveFilterToken("column:criteria");Check(grid.FilterPanel.Items.Single().Key=="criteria"&&grid.View!.Rows.Count==2);
        });
        Test("chrome.disabled-filters-workspace-and-replacement-table",()=>{
            using var grid=new GridControl{Table=ValidationSuite.Fixture()};grid.SetColumnFilter("Name","Alpha");grid.IsFilterEnabled=false;string state=grid.SaveLayout();
            grid.ClearFilters();grid.RestoreLayout(state);Check(!grid.IsFilterEnabled&&grid.View!.Rows.Count==4&&grid.FilterPanel.Items.Count==1);
            grid.Table=ValidationSuite.Fixture();Check(grid.IsFilterEnabled&&grid.FilterPanel.Items.Count==0);
        });
        Test("chrome.batch-filter-panel-shows-final-state-once",()=>{
            using var grid=new GridControl{Table=ValidationSuite.Fixture()};long count=grid.FilterPanel.ContentBuildCount;
            using(grid.BeginDataUpdate()){grid.SetColumnFilter("Name","Alpha");grid.SetColumnFilter("Region","West");grid.IsFilterEnabled=false;Check(grid.FilterPanel.ContentBuildCount==count);}
            Check(grid.FilterPanel.ContentBuildCount==count+1&&grid.FilterPanel.Items.Count==2&&!grid.IsFilterEnabled);
        });
        Test("chrome.ribbon-rejects-duplicate-commands-before-mutation",()=>{
            var ribbon=new CommandRibbon();ribbon.AddGroup("A",new RibbonCommand("test","Test",SceneGlyph.Table,()=>{}));
            try{ribbon.AddGroup("B",new("other","Other",SceneGlyph.Table,()=>{}),new("test","Duplicate",SceneGlyph.Table,()=>{}));}
            catch(ArgumentException){Check(ribbon.Commands.Count==1);return;}
            throw new InvalidOperationException("Duplicate ribbon command was accepted.");
        });
        Test("chrome.filter-panel-state-is-owned-and-idempotent",()=>{
            var panel=new GridFilterPanel();var tokens=new List<FilterPanelToken>{new("A","A","one")};panel.SetState(tokens,true);long count=panel.ContentBuildCount;
            tokens.Clear();Check(panel.Items.Count==1);panel.SetState(panel.Items,false);Check(count==panel.ContentBuildCount&&!panel.IsFilterEnabled);
            try{panel.SetState(new[]{new FilterPanelToken("A","A","one"),new FilterPanelToken("A","A","two")},true);}
            catch(ArgumentException){Check(panel.Items.Count==1&&!panel.IsFilterEnabled);return;}
            throw new InvalidOperationException("Duplicate token was accepted.");
        });
        Test("chrome.best-fit-and-idempotent-search-preserve-row-index",()=>{
            using var grid=new GridControl{Table=ValidationSuite.Fixture()};int changes=0;grid.ColumnLayout!.Changed+=(_,_)=>changes++;
            grid.BestFit();Check(changes==1);
            grid.SetSearch("Alpha");grid.SetSearch("");grid.SetRowHeight("row:1",62);
            long refresh=grid.View!.RefreshCount, rows=grid.RowGeometryBuildCount;
            for(int i=0;i<20;i++)grid.SetSearch("");
            Check(grid.View.RefreshCount==refresh&&grid.RowGeometryBuildCount==rows&&grid.HeightIndex[0]==62);
        });
        Test("chrome.best-fit-getter-failure-is-atomic",()=>{
            var table=new GridTable(new GridSchema([new("Name",typeof(string)),new("Calculated",typeof(string)){ValueGetter=_=>throw new InvalidOperationException("Expected failure")} ]));
            table.Add(new(1,new object?[]{"A very long value to resize",null}));using var grid=new GridControl{Table=table};var before=grid.ColumnLayout!.Columns;
            try{grid.BestFit();}catch(InvalidOperationException){Check(ReferenceEquals(before,grid.ColumnLayout.Columns));return;}
            throw new InvalidOperationException("Failing getter was ignored.");
        });
        return new(results.AsReadOnly(),total.Elapsed.TotalMilliseconds);
    }
}
