using DataGridScene.Controls;
using DataGridScene.Core;
using DataGridScene.Validation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;

namespace DataGridScene.Demo;

internal static class NativeControlChecks
{
    internal static ValidationReport Run()
    {
        var results = new List<ValidationResult>(); var clock = Stopwatch.StartNew();
        void Check(bool ok) { if (!ok) throw new InvalidOperationException("Native control contract failed."); }
        void Test(string name, Action action) {
            var start = Stopwatch.GetTimestamp();
            try { action(); results.Add(new(name,true,Stopwatch.GetElapsedTime(start).TotalMilliseconds,null)); }
            catch(Exception error) { results.Add(new(name,false,Stopwatch.GetElapsedTime(start).TotalMilliseconds,error.ToString())); }
        }
        var table = new GridTable(new GridSchema([new("Text",typeof(string)),new("Number",typeof(int)),new("Check",typeof(bool)),new("Date",typeof(DateTime)),new("Choice",typeof(string))]));
        table.Add(new(1,new object?[]{"Original",10,true,new DateTime(2026,10,11),"One"})); var history = new EditHistory(table);
        foreach (var (name,field,template,type) in new (string,string,GridCellTemplate,Type)[] {
            ("text","Text",GridCellTemplates.TextBox(),typeof(TextBox)),
            ("number","Number",GridCellTemplates.NumberBox(0,100),typeof(NumberBox)),
            ("check","Check",GridCellTemplates.CheckBox(),typeof(CheckBox)),
            ("choice","Choice",GridCellTemplates.ComboBox(new[]{"One","Two"}),typeof(ComboBox)),
            ("progress","Number",GridCellTemplates.ProgressBar(),typeof(ProgressBar)),
            ("slider","Number",GridCellTemplates.Slider(),typeof(Slider)),
            ("date","Date",GridCellTemplates.CalendarDatePicker(),typeof(CalendarDatePicker))
        }) Test("native-control."+name+"-is-real-native-and-binding-is-nonmutating",()=>{
            using var buffer = new GridCellEditBuffer(table,history,1,field); var control=template.Create();
            Check(control.GetType()==type); int version=table[1].Version;
            buffer.SynchronizeBinding(()=>{control.DataContext=buffer;template.Bind?.Invoke(control,buffer);});
            Check(!buffer.IsDirty&&table[1].Version==version&&!history.CanUndo);
            buffer.Dispose();template.Unbind?.Invoke(control,buffer);control.DataContext=null;template.Release?.Invoke(control);
        });
        Test("native-control.composite-datatemplate-creates-fresh-elements",()=>{
            var template=GridCellTemplate.FromDataTemplate(new DataTemplate(()=>{
                var panel=new StackPanel();panel.Children.Add(new TextBlock{Text="Label"});panel.Children.Add(new Button{Content="Action"});return panel;
            }));
            var a=template.Create();var b=template.Create();Check(!ReferenceEquals(a,b)&&a is StackPanel {Children.Count:2});
        });
        Test("native-control.number-flush-preserves-invalid-text",()=>{
            using var buffer=new GridCellEditBuffer(table,history,1,"Number");var template=GridCellTemplates.NumberBox(0,100);var control=(NumberBox)template.Create();
            buffer.SynchronizeBinding(()=>{control.DataContext=buffer;template.Bind!(control,buffer);});
            control.Text="fractional 1.5";template.Flush!(control,buffer);Check(buffer.IsDirty&&!buffer.TryCommit());
            template.Bind!(control,buffer);Check(control.Text=="fractional 1.5");buffer.Cancel();template.Bind!(control,buffer);Check(control.Value==10&&control.Text=="10");
        });
        Test("native-control.untouched-numeric-display-does-not-round-or-clamp",()=>{
            foreach (decimal value in new[] { 123456789.123456789m, 1000m, 0.000000000000000000000001m })
            {
                var source=new GridTable(new GridSchema([new("Exact",typeof(decimal))]));source.Add(new(1,new object?[]{value}));
                using var buffer=new GridCellEditBuffer(source,new(source),1,"Exact");var template=GridCellTemplates.NumberBox(0,100);
                var control=template.Create();buffer.SynchronizeBinding(()=>{control.DataContext=buffer;template.Bind!(control,buffer);});
                template.Flush!(control,buffer);Check(!buffer.IsDirty&&buffer.TryCommit()&&Equals(buffer.Value,value));
            }
        });
        Test("native-control.factory-ranges-validate-before-creation",()=>{
            int rejected=0;foreach(Action action in new Action[]{()=>GridCellTemplates.Slider(1,1),()=>GridCellTemplates.NumberBox(0,1,0),()=>GridCellTemplates.ProgressBar(double.NaN,100)})
                try{action();}catch(ArgumentOutOfRangeException){rejected++;}
            Check(rejected==3);
        });
        return new(results.AsReadOnly(),clock.Elapsed.TotalMilliseconds);
    }
}
