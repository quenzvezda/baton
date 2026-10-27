using Baton.Domain.Automations;

namespace Baton.Domain.Tests.Automations;

/// <summary>AUT-01, AUT-04..AUT-09: automation settings are validated field by field (M4 task 1.4).</summary>
public sealed class AutomationRulesTests
{
    private static readonly AutomationSettings Valid = new(
        "APAC nightly sales report", "Daily at 02:00 SGT", "ops@example.test", "nightly-sales-report", null, Guid.CreateVersion7(),
        ["region-apac"], 50, 1, "skip", TimeSpan.FromHours(2), [new TriggerSettings("cron", "0 2 * * *", "Asia/Singapore")]);

    [Fact]
    public void Valid_settings_have_no_errors()
    {
        Assert.Empty(AutomationRules.Validate(Valid));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(120)]
    public void Priority_outside_0_to_100_is_rejected(int priority)
    {
        Assert.Equal(["priority"], AutomationRules.Validate(Valid with { Priority = priority }).Keys);
    }

    [Fact]
    public void Each_invalid_setting_is_reported_by_field()
    {
        var errors = AutomationRules.Validate(Valid with
        {
            Name = " ",
            Owner = null,
            WorkflowKey = "",
            WorkflowVersion = 0,
            HostGroupId = null,
            RequiredLabels = ["has space"],
            MaxConcurrentRuns = 0,
            ConcurrencyPolicy = "fifo",
            RunTimeout = TimeSpan.Zero,
        });

        Assert.Equal(
            ["concurrencyPolicy", "hostGroupId", "maxConcurrentRuns", "name", "owner", "requiredLabels", "runTimeout", "workflowKey", "workflowVersion"],
            errors.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void An_automation_needs_a_trigger()
    {
        Assert.Equal(["triggers"], AutomationRules.Validate(Valid with { Triggers = [] }).Keys);
    }

    [Fact]
    public void Trigger_errors_name_the_trigger_and_field()
    {
        var errors = AutomationRules.Validate(Valid with
        {
            Triggers =
            [
                new TriggerSettings("webhook"),
                new TriggerSettings("cron", "0 2 * * *", "Not/AZone"),
                new TriggerSettings("email"),
                new TriggerSettings("manual", "0 2 * * *"),
            ],
        });

        Assert.Equal(["triggers[1].timeZone", "triggers[2].kind", "triggers[3].cronExpression"], errors.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void At_most_one_manual_trigger()
    {
        var errors = AutomationRules.Validate(Valid with { Triggers = [new TriggerSettings("manual"), new TriggerSettings("manual")] });

        Assert.Equal(["triggers"], errors.Keys);
    }

    [Fact]
    public void Limit_keys_follow_adr_0009()
    {
        var id = Guid.Parse("01890000-0000-7000-8000-000000000001");

        Assert.Equal("automation:01890000-0000-7000-8000-000000000001", AutomationRules.AutomationLimitKey(id));
        Assert.Equal("workflow:nightly-sales-report", AutomationRules.WorkflowLimitKey("nightly-sales-report"));
    }
}
