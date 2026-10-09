using DataGridScene.Core;
using DataGridScene.Rendering.Skia;
using System.Globalization;

namespace DataGridScene.Demo;

public static class OrderData
{
    private static readonly string[] Companies = ["Alpine Ski House", "Blue Yonder Airlines", "Coho Winery", "Contoso Retail", "Fabrikam Industries", "Fourth Coffee", "Graphic Design Institute", "Humongous Insurance", "Litware", "Northwind Traders", "Proseware", "Tailspin Toys", "Wide World Importers", "Woodgrove Bank", "Adventure Works", "Słoneczna Pracownia"];
    private static readonly string[] Cities = ["London", "Berlin", "Warszawa", "Paris", "Seattle", "Austin", "Toronto", "Tokyo", "Osaka", "Sydney", "Melbourne", "Auckland"];
    private static readonly string[] Regions = ["Europe", "North America", "Asia Pacific"];
    private static readonly string[] People = ["Olivia Martin", "Liam Anderson", "Emma Wilson", "Noah Thompson", "Amelia Davis", "Michał Nowak", "Charlotte Lee", "James Taylor"];
    public static readonly string[] Statuses = ["Shipped", "Processing", "Pending", "On hold", "Cancelled"];
    public static GridSchema CreateSchema() => new([
        new("OrderID", typeof(int), "Order ID") { Width = 92, ReadOnly = true, AllowNull = false, Band = "Order" },
        new("Company", typeof(string), "Company name") { Width = 220, AllowNull = false, Band = "Customer" },
        new("City", typeof(string)) { Width = 120, Band = "Customer" },
        new("Region", typeof(string)) { Width = 140, Choices = Regions, Band = "Customer" },
        new("Salesperson", typeof(string), "Sales representative") { Width = 174, Choices = People, Band = "Customer" },
        new("OrderDate", typeof(DateTime), "Order date") { Width = 116, Format = "dd MMM yyyy", Band = "Order" },
        new("Quantity", typeof(int)) { Width = 86, AllowNull = false, Band = "Order", Validator = value => value is int quantity && quantity is >= 1 and <= 100000 ? null : "Quantity must be from 1 to 100,000." },
        new("UnitPrice", typeof(decimal), "Unit price") { Width = 112, Format = "C2", AllowNull = false, Band = "Order", Validator = value => value is decimal amount && amount >= 0 ? null : "Unit price must be nonnegative." },
        new("Discount", typeof(decimal)) { Width = 96, Format = "P0", AllowNull = false, Band = "Order", Validator = value => value is decimal discount && discount is >= 0 and <= 1 ? null : "Discount must be from 0 to 1." },
        new("Total", typeof(decimal)) { Width = 138, Format = "C2", Band = "Order", ValueGetter = record => Math.Round((int)record[6]! * (decimal)record[7]! * (1m - (decimal)record[8]!), 2) },
        new("Progress", typeof(int), "Fulfilment") { Width = 120, Format = "0'%'", Presentation = ColumnPresentation.Progress, Band = "Delivery", Validator = value => value is int progress && progress is >= 0 and <= 100 ? null : "Fulfilment must be from 0 to 100." },
        new("Status", typeof(string)) { Width = 122, Choices = Statuses, Presentation = ColumnPresentation.Badge, Band = "Delivery" },
        new("Verified", typeof(bool)) { Width = 82, Band = "Delivery" },
        new("Trend", typeof(double[]), "Sales trend") { Width = 140, ReadOnly = true, Presentation = ColumnPresentation.Sparkline, Band = "Analytics" },
        new("Notes", typeof(string)) { Width = 260, Band = "Order" }
    ], CultureInfo.GetCultureInfo("en-US"));
    public static GridTable Create(int count)
    {
        if (count < 1 || count > 1_000_000) throw new ArgumentOutOfRangeException(nameof(count));
        var table = new GridTable(CreateSchema()); table.AddRange(Enumerable.Range(0, count).Select(CreateRecord)); return table;
    }
    public static GridRecord CreateRecord(int index)
    {
        int region = index % 3; int status = index % Statuses.Length;
        int quantity = 1 + index * 17 % 80;
        decimal price = 12.50m + index * 37 % 450 + index % 100 / 100m;
        int progress = status == 0 ? 100 : status == 4 ? 0 : index * 13 % 96;
        return new(10001L + index, new object?[]
        {
            10001 + index, Companies[index % Companies.Length], Cities[region * 4 + index / 3 % 4], Regions[region], People[index % People.Length],
            new DateTime(2026, 1, 1).AddDays(index % 280), quantity, price, index % 5 * 0.05m, null,
            progress, Statuses[status], index % 4 != 0,
            Enumerable.Range(0, 12).Select(month => 15d + (index * 11 + month * 17) % 60 + month * 2).ToArray(),
            index % 7 == 0 ? "Priority customer — confirm delivery window." : "Standard delivery"
        });
    }
    public static GridDetailData Details(GridRecord record)
    {
        int quantity = (int)record[6]!; decimal price = (decimal)record[7]!;
        return new(["Product / service", "Quantity", "Unit price", "Line total"], Enumerable.Range(0, 3).Select(i => (IReadOnlyList<string>)new[]
        {
            new[] { "Professional services", "Enterprise subscription", "Support & maintenance" }[i],
            Math.Max(1, quantity / 3 + (i == 0 ? quantity % 3 : 0)).ToString(), price.ToString("C2", CultureInfo.GetCultureInfo("en-US")),
            (Math.Max(1, quantity / 3) * price).ToString("C2", CultureInfo.GetCultureInfo("en-US"))
        }).ToArray());
    }
}
