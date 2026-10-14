using MultiKerbal.Common.Relay;
using MultiKerbal.Relay;

namespace MultiKerbal.Tests;

public sealed class CodeStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mk-codestore-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static byte[] Key(byte seed) => Enumerable.Range(0, 32).Select(i => (byte)(seed + i)).ToArray();

    [Fact]
    public void SameKeyGivesSameCode()
    {
        var store = new CodeStore(_dir);
        string first = store.GetOrAssign(Key(1));
        Assert.Equal(first, store.GetOrAssign(Key(1)));
        Assert.Equal(1, store.Count);
        Assert.True(ServerCode.TryNormalize(first, out string normalized));
        Assert.Equal(first, normalized);
    }

    [Fact]
    public void DifferentKeysGiveDifferentCodes()
    {
        var store = new CodeStore(_dir);
        var codes = new HashSet<string>();
        for (byte i = 0; i < 50; i++)
            Assert.True(codes.Add(store.GetOrAssign(Key(i))));
        Assert.Equal(50, store.Count);
    }

    [Fact]
    public void CodeAndBanSurviveRestart()
    {
        var store = new CodeStore(_dir);
        string code = store.GetOrAssign(Key(1));
        Assert.True(store.Ban(code));

        var reloaded = new CodeStore(_dir);
        Assert.Equal(code, reloaded.GetOrAssign(Key(1)));
        Assert.True(reloaded.IsBanned(code));
        Assert.Equal(1, reloaded.Count);

        Assert.True(reloaded.Unban(code));
        Assert.False(new CodeStore(_dir).IsBanned(code));
    }

    [Fact]
    public void FileDoesNotContainTheKey()
    {
        var store = new CodeStore(_dir);
        byte[] key = Key(7);
        store.GetOrAssign(key);

        string text = File.ReadAllText(Path.Combine(_dir, "codes.json"));
        Assert.DoesNotContain(Convert.ToHexString(key), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(key), text);
    }

    [Fact]
    public void BanOfUnknownCodeReturnsFalse()
    {
        var store = new CodeStore(_dir);
        Assert.False(store.Ban("K7MQ2P"));
        Assert.False(store.Unban("K7MQ2P"));
        Assert.False(store.IsBanned("K7MQ2P"));
    }

    [Fact]
    public void AcceptsDashedAndLowercaseCodes()
    {
        var store = new CodeStore(_dir);
        string code = store.GetOrAssign(Key(1));
        string dashed = ServerCode.Format(code).ToLowerInvariant();

        Assert.True(store.Ban(dashed));
        Assert.True(store.IsBanned(code));
        Assert.True(store.IsBanned(ServerCode.Format(code)));
        Assert.True(store.Unban(dashed));
        Assert.False(store.IsBanned(code));
        Assert.False(store.Ban("not a code"));
    }

    [Fact]
    public async Task ConcurrentAssignmentsAreConsistent()
    {
        var store = new CodeStore(_dir);
        string[] results = new string[40];
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() => results[i] = store.GetOrAssign(Key((byte)(i % 20))))));

        Assert.Equal(20, store.Count);
        Assert.Equal(20, results.Distinct().Count());
        for (int i = 0; i < 40; i++)
            Assert.Equal(results[i % 20], results[i]);
    }
}
