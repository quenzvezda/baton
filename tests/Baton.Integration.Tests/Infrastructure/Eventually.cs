namespace Baton.Integration.Tests.Infrastructure;

/// <summary>Polls until a condition holds, for tests that drive real loops and agents.</summary>
public static class Eventually
{
    public static async Task<T> UntilAsync<T>(Func<Task<T>> read, Func<T, bool> done, string what, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            var value = await read();
            if (done(value))
                return value;
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Timed out waiting for {what}; last value: {value}.");
            await Task.Delay(100);
        }
    }
}
