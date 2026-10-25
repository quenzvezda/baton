using Baton.Domain.Hosts;

namespace Baton.Domain.Tests.Hosts;

public sealed class AgentTokenTests
{
    [Fact]
    public void Generated_tokens_are_prefixed_url_safe_and_unique()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => AgentToken.Generate()).ToList();

        Assert.All(tokens, t =>
        {
            Assert.StartsWith(AgentToken.Prefix, t);
            Assert.Equal(AgentToken.Prefix.Length + 43, t.Length); // 32 bytes, base64url without padding
            Assert.DoesNotContain(t[AgentToken.Prefix.Length..], c => c is '+' or '/' or '=');
        });
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }

    [Fact]
    public void Hash_is_sha256_of_the_whole_token()
    {
        var hash = AgentToken.Hash("baton_abc");

        Assert.Equal("6f9d9cce92e2778ef51963948715c527a08f635019f349a0a32bf6b2f994f2dc", Convert.ToHexStringLower(hash));
    }
}
