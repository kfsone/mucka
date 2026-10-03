using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>The helper has to be able to fail: a store fault it swallowed would put the suite back
/// where it was.</summary>
public sealed class TestStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-teststore-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TestStore.ReleasePools(_directory);
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed record PoisonRow : IStoreRow
    {
        public void Write(StoreWrite write) => throw new InvalidOperationException("poisoned row");
    }

    [Fact]
    public void Open_ThrowsWhenTheStoreCannotOpen()
    {
        Directory.CreateDirectory(_directory);
        var blocked = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(blocked, "a file, so it cannot also be a directory");

        var ex = Assert.Throws<InvalidOperationException>(
            () => TestStore.Open(Path.Combine(blocked, MuckaDb.DefaultFileName)));
        Assert.Contains("nothing will be recorded", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void Close_ThrowsWhenTheWriterFaulted()
    {
        var store = TestStore.Open(Path.Combine(_directory, MuckaDb.DefaultFileName));
        store.Enqueue(new PoisonRow());

        var ex = Assert.Throws<InvalidOperationException>(() => TestStore.Close(store));
        Assert.Contains("poisoned row", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Close_IsQuietForAHealthyStore()
        => TestStore.Close(TestStore.Open(Path.Combine(_directory, MuckaDb.DefaultFileName)));
}
