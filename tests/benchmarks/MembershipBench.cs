using DataGridScene.Core;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
long getters=0; long comparisons=0;
var schema=new GridSchema([new("Value",typeof(int)) { ValueGetter=row=>{getters++;return (int)row.Id;}, Comparer=Comparer<object?>.Create((a,b)=>{comparisons++;return GridValue.Compare(a,b);}) }]);
var table=new GridTable(schema);table.AddRange(Enumerable.Range(0,10000).Select(i=>new GridRecord(i,new object?[]{null})));
var predicate=new FieldFilter("Value",FilterOperator.In,Values:Enumerable.Range(0,1024).Select(i=>(object?)(i*2)).ToArray()).Compile(schema);
int Scan(){int count=0;foreach(var row in table.Records)if(predicate(row))count++;return count;}
Scan();var rounds=new List<object>();
for(int i=0;i<5;i++){
 GC.Collect();GC.WaitForPendingFinalizers();GC.Collect(); getters=comparisons=0;long bytes=GC.GetAllocatedBytesForCurrentThread();var watch=Stopwatch.StartNew();int count=Scan();watch.Stop();
 if(count!=1024)throw new Exception("Incorrect membership result.");
 rounds.Add(new {milliseconds=watch.Elapsed.TotalMilliseconds,allocatedBytes=GC.GetAllocatedBytesForCurrentThread()-bytes,getters,comparisons,matches=count});
}
Console.WriteLine(JsonSerializer.Serialize(new{runtime=RuntimeInformation.FrameworkDescription,records=10000,candidates=1024,rounds},new JsonSerializerOptions{WriteIndented=true}));
