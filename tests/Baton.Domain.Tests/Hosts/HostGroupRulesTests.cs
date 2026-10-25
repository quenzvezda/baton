using Baton.Domain.Hosts;

namespace Baton.Domain.Tests.Hosts;

public sealed class HostGroupRulesTests
{
    [Theory]
    [InlineData("etl", true)]
    [InlineData("gpu-2", true)]
    [InlineData("0day", true)]
    [InlineData("", false)]
    [InlineData("-etl", false)]
    [InlineData("ETL", false)]
    [InlineData("etl_1", false)]
    [InlineData("etl 1", false)]
    public void Group_names_are_lowercase_letters_digits_and_dashes(string name, bool valid)
    {
        Assert.Equal(valid, HostGroupRules.IsValidName(name));
    }

    [Fact]
    public void Group_names_are_at_most_63_characters()
    {
        Assert.True(HostGroupRules.IsValidName(new string('a', 63)));
        Assert.False(HostGroupRules.IsValidName(new string('a', 64)));
    }

    [Fact]
    public void Absent_limits_mean_unlimited()
    {
        Assert.Empty(HostGroupRules.Validate("etl", null, null, HostGroupRules.DefaultLeaseSeconds));
    }

    [Fact]
    public void Each_invalid_setting_is_reported_by_field()
    {
        var errors = HostGroupRules.Validate("Bad Name", 0, -1, 10);

        Assert.Equal(["leaseSeconds", "maxConcurrentRuns", "maxConcurrentTasks", "name"], errors.Keys.Order());
    }

    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(3600, true)]
    [InlineData(3601, false)]
    public void Lease_is_30_seconds_to_an_hour(int seconds, bool valid)
    {
        Assert.Equal(valid, HostGroupRules.Validate("etl", null, null, seconds).Count == 0);
    }

    [Fact]
    public void Limit_keys_follow_adr_0009()
    {
        var id = Guid.Parse("01900000-0000-7000-8000-000000000001");

        Assert.Equal("group-runs:01900000-0000-7000-8000-000000000001", HostGroupRules.RunsLimitKey(id));
        Assert.Equal("group-tasks:01900000-0000-7000-8000-000000000001", HostGroupRules.TasksLimitKey(id));
    }
}
