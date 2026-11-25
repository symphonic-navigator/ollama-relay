using OllamaRelay.Relay;
using OllamaRelay.Relay.Security;

namespace OllamaRelay.UnitTests;

public class SecurityTests
{
    private static readonly RelayOptions Options = new()
    {
        JwtSigningKey = new string('k', 48),
        Argon2MemoryKib = 1024,
        Argon2Iterations = 1,
    };

    [Fact]
    public void Argon2id_hash_verifies_and_stores_parameters()
    {
        var hasher = new PasswordHasher(Options);
        var hash = hasher.Hash("correct horse battery staple");
        Assert.StartsWith("$argon2id$v=19$m=1024,t=1,p=1$", hash);
        Assert.True(hasher.Verify("correct horse battery staple", hash));
        Assert.False(hasher.Verify("wrong", hash));
        Assert.NotEqual(hash, hasher.Hash("correct horse battery staple"));
    }

    [Fact]
    public void Password_policy_is_length_only()
    {
        Assert.NotNull(PasswordHasher.ValidatePolicy("short"));
        Assert.Null(PasswordHasher.ValidatePolicy("alllowercaseletters"));
    }

    [Fact]
    public void Api_keys_have_prefix_and_256_bits()
    {
        var key = SecretTokens.NewApiKey();
        Assert.Matches("^orl_[0-9a-f]{8}_[0-9a-f]{64}$", key.Secret);
        Assert.StartsWith(key.Prefix + "_", key.Secret);
        Assert.Equal(SecretTokens.Hash(key.Secret), key.Hash);
        Assert.Matches("^orn_[0-9a-f]{8}_[0-9a-f]{64}$", SecretTokens.NewNodeToken().Secret);
    }

    [Fact]
    public void Attempt_limiter_blocks_after_threshold()
    {
        var limiter = new AttemptLimiter(3, TimeSpan.FromMinutes(1));
        for (var i = 0; i < 3; i++)
        {
            Assert.False(limiter.IsBlocked("ip"));
            limiter.RecordFailure("ip");
        }

        Assert.True(limiter.IsBlocked("ip"));
        Assert.False(limiter.IsBlocked("other"));
        limiter.Reset("ip");
        Assert.False(limiter.IsBlocked("ip"));
    }
}
