using DataGridScene.Core;
using DataGridScene.Data;
using System.Diagnostics;

namespace DataGridScene.Validation;

/// <summary>Deterministic request, cancellation and consistency contracts; no timing speed thresholds.</summary>
public static class PageSchedulingValidation
{
    private static void Check(bool condition, string message = "Assertion failed.")
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Reject<T>(Task task) where T : Exception
    { try { await task.WaitAsync(TimeSpan.FromSeconds(3)); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Until(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate()) { if (timer.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException(); await Task.Delay(1); }
    }
    public static async Task<ValidationReport> AppendAsync(ValidationReport previous)
    {
        var clock = Stopwatch.StartNew(); var results = previous.Results.ToList();
        async Task Test(string name, Func<Task> action)
        {
            var timer = Stopwatch.StartNew();
            try { await action().WaitAsync(TimeSpan.FromSeconds(8)); results.Add(new(name,true,timer.Elapsed.TotalMilliseconds,null)); }
            catch(Exception e) { results.Add(new(name,false,timer.Elapsed.TotalMilliseconds,e.ToString())); }
        }
        await Test("pages.pre-cancelled-does-not-start-io", async () => {
            var source=new Controlled();using var cache=new GridPageCache(source);using var cancel=new CancellationTokenSource();cancel.Cancel();
            await Reject<OperationCanceledException>(cache.GetPageAsync(0,cancel.Token));Check(source.Count==0 && cache.PendingPageCount==0);
        });
        await Test("pages.inflight-coalesces-independent-of-lru-capacity", async () => {
            var source=new Controlled();using var cache=new GridPageCache(source,1,1);
            var first=cache.GetPageAsync(0);var second=cache.GetPageAsync(1);var third=cache.GetPageAsync(2);var again=cache.GetPageAsync(0);
            Check(source.Count==3 && ReferenceEquals(first,again));Check(cache.CoalescedReadCount==1);
            for(int i=0;i<3;i++)source.Complete(i);await Task.WhenAll(first,second,third);Check(cache.CachedPageCount==1);
        });
        await Test("pages.request-concurrency-and-fifo-queue", async () => {
            var source=new Controlled();using var cache=new GridPageCache(source,1,2,2,10);
            var tasks=Enumerable.Range(0,6).Select(i=>cache.GetPageAsync(i)).ToArray();Check(source.Count==2 && cache.ActiveFetchCount==2 && cache.PendingPageCount==6);
            for(int i=0;i<6;i++){await Until(()=>source.Count>i);Check(cache.ActiveFetchCount<=2);source.Complete(i);}
            await Task.WhenAll(tasks);Check(cache.PendingPageCount==0 && cache.ActiveFetchCount==0 && cache.CachedPageCount==2);
            Check(source.Requests.Select(r=>r.Offset).SequenceEqual(Enumerable.Range(0,6)));
        });
        await Test("pages.queue-bound-does-not-break-existing-waiters", async () => {
            var source=new Controlled();using var cache=new GridPageCache(source,1,1,1,2);
            var a=cache.GetPageAsync(0);var b=cache.GetPageAsync(1);Throws<InvalidOperationException>(()=>cache.GetPageAsync(2));
            Check(ReferenceEquals(a,cache.GetPageAsync(0)));source.Complete(0);await Until(()=>source.Count==2);source.Complete(1);await Task.WhenAll(a,b);
        });
        await Test("pages.query-cancels-active-and-queued-waiters-immediately", async () => {
            var source=new Controlled();using var cache=new GridPageCache(source,1,1,1,8);
            var a=cache.GetPageAsync(0);var b=cache.GetPageAsync(1);cache.SetQuery(new(){Search="next"});
            await Reject<OperationCanceledException>(a);await Reject<OperationCanceledException>(b);
            var next=cache.GetPageAsync(0);Check(source.Count==1 && cache.ActiveFetchCount==1);
            source.Complete(0);await Until(()=>source.Count==2);Check(source.Requests[1].Query.Search=="next");source.Complete(1);await next;
        });
        await Test("pages.disposal-cancels-queued-and-active", async () => {
            var source=new Controlled();var cache=new GridPageCache(source,1,1,1,8);
            var a=cache.GetPageAsync(0);var b=cache.GetPageAsync(1);cache.Dispose();cache.Dispose();
            await Reject<OperationCanceledException>(a);await Reject<OperationCanceledException>(b);source.Complete(0);
            await Until(()=>cache.ActiveFetchCount==0);Check(source.Count==1);Throws<ObjectDisposedException>(()=>cache.GetPageAsync(0));
        });
        await Test("pages.failed-request-can-be-retried", async () => {
            var source=new Controlled();using var cache=new GridPageCache(source,1);
            var first=cache.GetPageAsync(0);source.Fail(0);await Reject<IOException>(first);
            var second=cache.GetPageAsync(0);Check(source.Count==2);source.Complete(1);await second;Check(cache.CachedPageCount==1);
        });
        await Test("pages.mixed-revision-fails-closed-until-refresh", async () => {
            var source=new Controlled();using var cache=new GridPageCache(source,1);
            var first=cache.GetPageAsync(0);source.Complete(0,revision:"A");await first;
            var second=cache.GetPageAsync(1);var queued=cache.GetPageAsync(2);source.Complete(1,revision:"B");
            await Reject<InvalidDataException>(second);await Reject<InvalidDataException>(queued);Check(cache.CachedPageCount==0);
            await Reject<InvalidDataException>(cache.GetPageAsync(0));source.Complete(2,revision:"B");
            cache.SetQuery(new());var next=cache.GetPageAsync(0);await Until(()=>source.Count==4);source.Complete(3,revision:"B");await next;
        });
        await Test("pages.mixed-totals-fail-even-with-matching-revision", async () => {
            var source=new Controlled();using var cache=new GridPageCache(source,1);
            var a=cache.GetPageAsync(0);source.Complete(0,total:100);await a;
            var b=cache.GetPageAsync(1);source.Complete(1,total:101);await Reject<InvalidDataException>(b);Check(cache.CachedPageCount==0);
        });
        await Test("pages.missing-revision-does-not-mix-with-versioned-data", async () => {
            var source=new Controlled();using var cache=new GridPageCache(source,1);
            var a=cache.GetPageAsync(0);source.Complete(0,revision:"A");await a;
            var b=cache.GetPageAsync(1);source.Complete(1,revision:null);await Reject<InvalidDataException>(b);
        });
        await Test("pages.unknown-count-can-be-learned-consistently", async () => {
            var source=new Controlled();using var cache=new GridPageCache(source,1);
            var a=cache.GetPageAsync(0);source.Complete(0,total:null);await a;
            var b=cache.GetPageAsync(1);source.Complete(1,total:10);await b;Check(cache.CachedPageCount==2);
        });
        await Test("pages.learned-total-cannot-contradict-retained-page", async () => {
            var source=new Controlled();using var cache=new GridPageCache(source,1);
            var a=cache.GetPageAsync(5);source.Complete(0,total:null);await a;
            var b=cache.GetPageAsync(0);source.Complete(1,total:2);await Reject<InvalidDataException>(b);
        });
        await Test("pages.record-list-detached-and-readonly", async () => {
            var source=new Controlled();using var cache=new GridPageCache(source,1);
            var rows=new List<GridRecord>{new(0,new object?[]{0})};var task=cache.GetPageAsync(0);source.Requests[0].Result.SetResult(new(0,rows,100,"A"));
            var page=await task;rows.Clear();Check(page.Records.Count==1);Throws<NotSupportedException>(()=>((IList<GridRecord>)page.Records).Clear());
        });
        await Test("pages.invalid-record-width-is-rejected", async () => {
            var source=new Controlled();using var cache=new GridPageCache(source,1);
            var task=cache.GetPageAsync(0);source.Requests[0].Result.SetResult(new(0,[new(1,new object?[]{1,2})],100));await Reject<InvalidDataException>(task);
        });
        await Test("pages.duplicate-identities-in-page-rejected", async () => {
            var source=new Controlled();using var cache=new GridPageCache(source,2);
            var task=cache.GetPageAsync(0);source.Requests[0].Result.SetResult(new(0,[new(1,new object?[]{1}),new(1,new object?[]{1})],100));await Reject<InvalidDataException>(task);
        });
        await Test("pages.int-boundary-does-not-wrap-metadata-check", async () => {
            var source=new Controlled();using var cache=new GridPageCache(source,1);
            var task=cache.GetPageAsync(int.MaxValue);source.Complete(0,total:int.MaxValue);await Reject<InvalidDataException>(task);
        });
        await Test("pages.completed-lru-recency-and-hit-count", async () => {
            var table=new GridTable(new GridSchema([new("Value",typeof(int))]));table.AddRange(Enumerable.Range(0,5).Select(i=>new GridRecord(i,new object?[]{i})));
            using var cache=new GridPageCache(new LocalPageSource(table),1,2);
            await cache.GetPageAsync(0);await cache.GetPageAsync(1);await cache.GetPageAsync(0);await cache.GetPageAsync(2);await cache.GetPageAsync(0);
            Check(cache.FetchCount==3 && cache.CacheHitCount==2);await cache.GetPageAsync(1);Check(cache.FetchCount==4);
        });
        await Test("pages.initial-query-shapes-once", () => {
            int evaluations=0;var table=new GridTable(new GridSchema([new("Value",typeof(int)){ValueGetter=row=>{evaluations++;return row.Id;}}]));
            table.AddRange(Enumerable.Range(0,100).Select(i=>new GridRecord(i,new object?[]{null})));
            using var view=new GridView(table,new(){Sorts=[new("Value",SortDirection.Descending)]});
            Check(view.RefreshCount==1 && evaluations==100 && view.Rows[0].Record!.Id==99);return Task.CompletedTask;
        });
        await Test("pages.cancel-one-shared-waiter-does-not-cancel-source", async () => {
            var source=new Controlled();using var cache=new GridPageCache(source,1);using var cancel=new CancellationTokenSource();
            var first=cache.GetPageAsync(0,cancel.Token);var second=cache.GetPageAsync(0);cancel.Cancel();await Reject<OperationCanceledException>(first);
            Check(!source.Requests[0].Token.IsCancellationRequested);source.Complete(0);await second;Check(source.Count==1);
        });
        await Test("pages.cancellation-callback-failure-does-not-strand-query", async () => {
            var source=new Controlled();using var cache=new GridPageCache(source,1);var first=cache.GetPageAsync(0);
            using var registration=source.Requests[0].Token.Register(()=>throw new ApplicationException("bad source callback"));
            cache.SetQuery(new(){Search="new"});await Reject<OperationCanceledException>(first);
            var next=cache.GetPageAsync(0);source.Complete(0);source.Complete(1);await next;Check(cache.CachedPageCount==1);
        });
        await Test("pages.query-snapshot-detaches-caller-collections", async () => {
            var source=new Controlled();using var cache=new GridPageCache(source,1);var sorts=new List<SortDescriptor>{new("Value")};cache.SetQuery(new(){Sorts=sorts});sorts.Clear();
            var page=cache.GetPageAsync(0);Check(source.Requests[0].Query.Sorts.Count==1);source.Complete(0);await page;
        });
        await Test("pages.constructor-and-queue-limits-validated", () => {
            var source=new Controlled();Throws<ArgumentOutOfRangeException>(()=>new GridPageCache(source,1,1,0,1));
            Throws<ArgumentOutOfRangeException>(()=>new GridPageCache(source,1,1,4,2));Throws<ArgumentNullException>(()=>new GridPageCache(null!));return Task.CompletedTask;
        });
        return new(results.AsReadOnly(),previous.Milliseconds+clock.Elapsed.TotalMilliseconds);
    }
    private sealed record Request(GridQuery Query,int Offset,int Count,CancellationToken Token)
    {
        public TaskCompletionSource<GridPage> Result { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Controlled : IGridPageSource
    {
        private readonly List<Request> _requests=[];
        public GridSchema Schema { get; }=new([new("Value",typeof(int))]);
        public DataCapabilities Capabilities=>DataCapabilities.Sort|DataCapabilities.Search;
        public Request[] Requests { get { lock(_requests)return _requests.ToArray(); } }
        public int Count { get { lock(_requests)return _requests.Count; } }
        public ValueTask<GridPage> FetchAsync(GridQuery query,int offset,int count,CancellationToken token)
        {
            var request=new Request(query,offset,count,token);lock(_requests)_requests.Add(request);
            // Intentionally ignore cancellation to verify stale-data suppression.
            return new(request.Result.Task);
        }
        public void Complete(int index,string? revision="A",int? total=100)
        {
            var r=Requests[index];r.Result.TrySetResult(new(r.Offset,[new GridRecord(r.Offset,new object?[]{r.Offset})],total,revision));
        }
        public void Fail(int index)=>Requests[index].Result.TrySetException(new IOException("Source failed"));
    }
}
