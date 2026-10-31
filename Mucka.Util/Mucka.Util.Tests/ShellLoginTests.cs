using Mucka.Commands;

namespace Mucka.Util.Tests;

/// <summary>
/// <see cref="ShellLogin"/>. The case that mattered: a profile left with loginname=mudguest and an
/// account filled in was logged in as a guest over both SSH and telnet, and the account was never
/// asked for.
/// </summary>
public class ShellLoginTests
{
    [Theory]
    [InlineData("mudguest", true, "mud")]      // an account cannot log in through the guest shell
    [InlineData("MudGuest", true, "mud")]
    [InlineData("mudguest", false, "mudguest")] // a guest or hand login keeps it
    [InlineData("mud", true, "mud")]
    [InlineData("testshell", true, "testshell")] // a server's own name is respected
    [InlineData(" testshell ", false, "testshell")]
    [InlineData("", true, "mud")]
    [InlineData(null, false, "mud")]
    public void TheLoginPresented(string? stored, bool accountLogin, string expected)
        => Assert.Equal(expected, ShellLogin.For(stored, accountLogin));
}
