using Mucka.Commands;
using Xunit;

namespace Mucka.Util.Tests;

public class PasswordStorageTests
{
    [Theory]
    [InlineData("hunter2")]
    [InlineData("a b ; c # d")]
    [InlineData(" lead and trail ")]
    [InlineData("50%off %41 %")]
    [InlineData("tab\there\r\nnewline")]
    [InlineData("caf\u00e9 \U0001F600")]
    [InlineData("pa#ss;word")]
    public void EncodedPasswordSurvivesAnIniLine(string password)
    {
        var value = IniPasswordCodec.Encode(password);
        // What the ini reader would hand back for "password=<value>": trimmed, cut at " ;" or " #".
        Assert.Equal(value.Trim(), value);
        Assert.DoesNotContain(value, c => c is '\r' or '\n' or ';' or '#');
        Assert.Equal(password, IniPasswordCodec.Decode(value));
    }

    [Fact]
    public void PlainPasswordIsWrittenAsTyped() => Assert.Equal("hunter2", IniPasswordCodec.Encode("hunter2"));

    [Fact]
    public void HandEditedPercentWithoutHexIsKept() => Assert.Equal("100%", IniPasswordCodec.Decode("100%"));

    [Fact]
    public async Task PasswordIsWrittenBeforeItIsRemovedFromTheSource()
    {
        var log = new List<string>();
        var result = await PasswordMigration.MoveAsync(["a"],
            n => { log.Add("read"); return Task.FromResult<string?>("pw"); },
            (n, p) => { log.Add("write"); return Task.CompletedTask; },
            n => { log.Add("remove"); return Task.CompletedTask; });
        Assert.Equal(["read", "write", "remove"], log);
        Assert.Equal(new PasswordMigration.Result(1, 0, 0), result);
    }

    [Fact]
    public async Task FailedWriteLeavesTheSourceAlone()
    {
        var removed = new List<string>();
        var result = await PasswordMigration.MoveAsync(["a", "b"],
            n => Task.FromResult<string?>("pw"),
            (n, p) => n == "a" ? throw new InvalidOperationException() : Task.CompletedTask,
            n => { removed.Add(n); return Task.CompletedTask; });
        Assert.Equal(["b"], removed);
        Assert.Equal(new PasswordMigration.Result(1, 0, 1), result);
    }

    [Fact]
    public async Task DeniedReadMovesNothingAndGoesOn()
    {
        var written = new List<string>();
        var result = await PasswordMigration.MoveAsync(["a", "b", "c"],
            n => n switch { "a" => throw new InvalidOperationException(), "b" => Task.FromResult<string?>(null), _ => Task.FromResult<string?>("pw") },
            (n, p) => { written.Add(n); return Task.CompletedTask; },
            n => Task.CompletedTask);
        Assert.Equal(["c"], written);
        Assert.Equal(new PasswordMigration.Result(1, 1, 1), result);
    }
}
