using System;
using System.Text.Json;
using DataGridScene.Core;

// Identical caller code against the old and new Core DLLs. No timing threshold.
int getterCalls = 0;
var table = new GridTable(new GridSchema(new GridColumn[] {
    new("Value", typeof(string)) { ValueGetter = row => { getterCalls++; return row[0]; } }
}));
table.Add(new GridRecord(1, new object?[] { "unchanged" }));
using var buffer = new GridCellEditBuffer(table, new EditHistory(table), 1, "Value");
int notifications = 0;
buffer.PropertyChanged += (_, _) => notifications++;
getterCalls = 0;
for (int i = 0; i < 1000; i++) {
    if (!buffer.TryCommit()) throw new Exception(buffer.Error);
    buffer.Refresh();
}
Console.WriteLine(JsonSerializer.Serialize(new {
    iterations = 1000, notifications, getterCalls, dirty = buffer.IsDirty,
    value = buffer.Value, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
}));
