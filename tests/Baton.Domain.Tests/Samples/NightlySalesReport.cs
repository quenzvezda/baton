using Baton.Abstractions;
using Microsoft.Extensions.Logging;

namespace Baton.Domain.Tests.Samples;

// The design spec §5 "Example definition", kept verbatim so the SDK keeps compiling it (M1 task 1.1).
// Below the example: the minimal types it refers to but the spec leaves out.

public sealed record SalesReportParams(string Region, DateOnly? ReportDate, string[] Recipients);

public sealed class NightlySalesReport : WorkflowDefinition<SalesReportParams>
{
    public override string Key => "nightly-sales-report";
    public override int Version => 2;

    protected override void Define(IWorkflowBuilder<SalesReportParams> wf)
    {
        var orders = wf.Task<ExtractOrders>("extract-orders")
            .Retry(maxAttempts: 4, initialDelay: TimeSpan.FromSeconds(30))
            .Timeout(TimeSpan.FromMinutes(15));

        var customers = wf.Task<ExtractCustomers>("extract-customers")
            .Retry(3, TimeSpan.FromSeconds(30));

        var report = wf.Task<BuildReport>("build-report")
            .DependsOn(orders, customers)
            .RequireLabels("large-memory");

        wf.Task<PublishReport>("publish-report").DependsOn(report);

        wf.Task<NotifyOnFailure>("notify-failure")
            .DependsOn(orders, customers, report)
            .When(TriggerRule.OneFailed);
    }
}

public sealed class BuildReport(IReportRenderer renderer)
    : ITaskHandler<SalesReportParams, ReportOutput>
{
    public async Task<ReportOutput> ExecuteAsync(
        TaskContext<SalesReportParams> ctx, CancellationToken ct)
    {
        var orders = ctx.GetOutput<OrdersExtract>("extract-orders");
        var customers = ctx.GetOutput<CustomersExtract>("extract-customers");
#pragma warning disable CA1848, CA1873 // verbatim spec example; repo code uses [LoggerMessage]
        ctx.Logger.LogInformation("Rendering {Count} orders for {Region}",
            orders.Count, ctx.Params.Region);
#pragma warning restore CA1848, CA1873

        var uri = await renderer.RenderAsync(orders, customers, ctx.IdempotencyKey, ct);
        return new ReportOutput(uri);
    }
}

public sealed record OrdersExtract(int Count);

public sealed record CustomersExtract(int Count);

public sealed record ReportOutput(Uri Location);

public sealed record Published(Uri Location);

public sealed record Notified(int Recipients);

public interface IReportRenderer
{
    Task<Uri> RenderAsync(OrdersExtract orders, CustomersExtract customers, string idempotencyKey, CancellationToken ct);
}

public sealed class ExtractOrders : ITaskHandler<SalesReportParams, OrdersExtract>
{
    public Task<OrdersExtract> ExecuteAsync(TaskContext<SalesReportParams> ctx, CancellationToken ct) =>
        Task.FromResult(new OrdersExtract(0));
}

public sealed class ExtractCustomers : ITaskHandler<SalesReportParams, CustomersExtract>
{
    public Task<CustomersExtract> ExecuteAsync(TaskContext<SalesReportParams> ctx, CancellationToken ct) =>
        Task.FromResult(new CustomersExtract(0));
}

public sealed class PublishReport : ITaskHandler<SalesReportParams, Published>
{
    public Task<Published> ExecuteAsync(TaskContext<SalesReportParams> ctx, CancellationToken ct) =>
        Task.FromResult(new Published(ctx.GetOutput<ReportOutput>("build-report").Location));
}

public sealed class NotifyOnFailure : ITaskHandler<SalesReportParams, Notified>
{
    public Task<Notified> ExecuteAsync(TaskContext<SalesReportParams> ctx, CancellationToken ct) =>
        Task.FromResult(new Notified(ctx.Params.Recipients.Length));
}
