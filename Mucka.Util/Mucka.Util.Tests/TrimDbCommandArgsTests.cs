using Mucka.Commands;

namespace Mucka.Util.Tests;

public class TrimDbCommandArgsTests
{
    [Theory]
    [InlineData("trim", TrimDbVerb.Trim, 30)]
    [InlineData("trim 7", TrimDbVerb.Trim, 7)]
    [InlineData("  TRIM   0 ", TrimDbVerb.Trim, 0)]
    [InlineData("unwrap", TrimDbVerb.Unwrap, 30)]
    [InlineData("unwrap 60", TrimDbVerb.Unwrap, 60)]
    [InlineData("expire", TrimDbVerb.Expire, 30)]
    [InlineData("expire 90", TrimDbVerb.Expire, 90)]
    [InlineData("revert", TrimDbVerb.Revert, 0)]
    [InlineData("optimize", TrimDbVerb.Optimize, 0)]
    public void AValidArgument_Parses(string argument, TrimDbVerb verb, int days)
    {
        var parsed = TrimDbCommandArgs.Parse(argument, defaultDays: 30, out var error);

        Assert.Null(error);
        Assert.Equal(new TrimDbCommandArgs(verb, days), parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("vacuum")]
    [InlineData("trim soon")]
    [InlineData("trim -5")]
    [InlineData("trim 1.5")]
    [InlineData("trim 99999999999")]
    [InlineData("trim 3651")]
    [InlineData("trim 7 8")]
    [InlineData("revert 7")]
    [InlineData("optimize now")]
    public void AnythingElse_IsRefusedWithAReason(string argument)
    {
        var parsed = TrimDbCommandArgs.Parse(argument, defaultDays: 30, out var error);

        Assert.Null(parsed);
        Assert.False(string.IsNullOrEmpty(error));
    }
}
