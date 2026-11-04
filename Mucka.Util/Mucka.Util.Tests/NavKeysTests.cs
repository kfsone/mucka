using Mucka.Commands;
using Mucka.Settings;

namespace Mucka.Util.Tests;

/// <summary>
/// <see cref="NavKeyMap"/>, <see cref="NavKeyBindings"/>, <see cref="KeyCollision"/> and
/// <see cref="NavScanGate"/>: the navigation options and their default number-pad keys, how an edited
/// set is stored and checked, the quick-scan bytes, and the scan's once-a-second spacing.
/// </summary>
public sealed class NavKeysTests
{
    private static readonly Func<int, bool> NoMacros = _ => false;

    [Fact]
    public void EveryOptionIsListedOnce()
    {
        var actions = NavKeyMap.Actions.Select(a => a.Action).ToList();
        Assert.Equal(Enum.GetValues<NavAction>().Length, actions.Count);
        Assert.Equal(actions.Count, actions.Distinct().Count());
    }

    [Theory]
    [InlineData(NavAction.NorthWest, "Pad7", "northwest")]
    [InlineData(NavAction.North, "Pad8", "north")]
    [InlineData(NavAction.NorthEast, "Pad9", "northeast")]
    [InlineData(NavAction.West, "Pad4", "west")]
    [InlineData(NavAction.East, "Pad6", "east")]
    [InlineData(NavAction.SouthWest, "Pad1", "southwest")]
    [InlineData(NavAction.South, "Pad2", "south")]
    [InlineData(NavAction.SouthEast, "Pad3", "southeast")]
    [InlineData(NavAction.Out, "Pad0", "out")]
    [InlineData(NavAction.In, "PadMultiply", "in")]
    [InlineData(NavAction.Up, "PadSubtract", "up")]
    [InlineData(NavAction.Down, "PadAdd", "down")]
    [InlineData(NavAction.Swampward, "PadPeriod", "zw")]
    public void DefaultKeysAndLines(NavAction action, string key, string line)
    {
        Assert.Equal(key, NavKeyBindings.Default.Get(action));
        Assert.Equal(line, NavKeyMap.For(action).Command);
    }

    [Fact]
    public void ScanIsOnFiveAndSendsNoLine()
    {
        Assert.Equal("Pad5", NavKeyBindings.Default.Get(NavAction.Scan));
        Assert.Equal(string.Empty, NavKeyMap.For(NavAction.Scan).Command);
    }

    [Fact]
    public void TheDefaultsAreClaimableAndShareNoKey()
    {
        Assert.Empty(NavKeyBindings.Default.Problems(_ => true));
        Assert.Equal(NavKeyMap.Actions.Count, NavKeyBindings.Default.Claims(_ => true).Count);
    }

    [Fact]
    public void EveryChoiceHasAUniqueName_AndTheDefaultsAreAmongThem()
    {
        var names = NavKeyMap.Choices.Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(NavKeyMap.Actions, a => Assert.NotNull(NavKeyMap.FindChoice(a.DefaultKey)));
    }

    [Fact]
    public void ChoicesAreTheNumberPadAndFunctionKeys()
        => Assert.All(NavKeyMap.Choices, c => Assert.True(
            c.Key.Kind == KeyKind.Other && c.Name.StartsWith("Pad", StringComparison.Ordinal)
            || c.Key.Kind == KeyKind.Function && c.Name == $"F{c.Key.FunctionNumber}"));

    [Fact]
    public void ScanIsACommandInterrupt()
    {
        Assert.Equal(
            new byte[] { 0x1b, (byte)'-', (byte)'[', (byte)'q', (byte)'u', (byte)'i', (byte)'c', (byte)'k',
                         (byte)'s', (byte)'c', (byte)'a', (byte)'n', 0x1b, (byte)'-', (byte)']' },
            NavKeyMap.ScanBytes);
    }

    [Fact]
    public void FindChoiceIgnoresCaseAndPadding_AndKnowsNoneWhenUnknown()
    {
        Assert.Equal("PadAdd", NavKeyMap.FindChoice("  padadd ")?.Name);
        Assert.Null(NavKeyMap.FindChoice("A"));
        Assert.Null(NavKeyMap.FindChoice(""));
        Assert.Null(NavKeyMap.FindChoice(null));
    }

    [Fact]
    public void AnEditMakesANewSetAndLeavesTheDefaultAlone()
    {
        var edited = NavKeyBindings.Default.With(NavAction.North, "f5").With(NavAction.South, "Enter");

        Assert.Equal("F5", edited.Get(NavAction.North));
        Assert.Equal("", edited.Get(NavAction.South));   // not a key that can be offered: none
        Assert.Equal("Pad8", NavKeyBindings.Default.Get(NavAction.North));
        Assert.False(edited.IsSameAs(NavKeyBindings.Default));
        Assert.True(edited.With(NavAction.North, "Pad8").With(NavAction.South, "Pad2").IsSameAs(NavKeyBindings.Default));
    }

    [Fact]
    public void AnFkeyWithAMacroIsAProblem_AndOneWithoutIsClaimed()
    {
        var edited = NavKeyBindings.Default.With(NavAction.North, "F2").With(NavAction.South, "F3");
        bool HasMacro(int n) => n == 2;

        var problems = edited.Problems(HasMacro);

        Assert.Equal(["North cannot use F2: F2 has a macro."], problems);
        var claims = edited.Claims(HasMacro);
        Assert.DoesNotContain(claims, c => c.Action == NavAction.North);
        Assert.Contains(claims, c => c.Action == NavAction.South && c.Key.Name == "F3");
    }

    [Fact]
    public void TwoOptionsOnOneKeyAreAProblem_AndTheFirstOptionKeepsIt()
    {
        var edited = NavKeyBindings.Default.With(NavAction.South, "Pad8");

        Assert.Equal(["North and South both use Numpad 8."], edited.Problems(NoMacros));
        var claims = edited.Claims(NoMacros);
        Assert.Equal(NavAction.North, claims.Single(c => c.Key.Name == "Pad8").Action);
        Assert.DoesNotContain(claims, c => c.Action == NavAction.South);
    }

    [Fact]
    public void AnOptionWithNoKeyIsNotAProblem()
    {
        var edited = NavKeyBindings.Default.With(NavAction.Scan, "");

        Assert.Empty(edited.Problems(NoMacros));
        Assert.DoesNotContain(edited.Claims(NoMacros), c => c.Action == NavAction.Scan);
    }

    [Fact]
    public void KeysAreNamedForTheirOption()
    {
        Assert.Equal("northwest", NavKeyBindings.IniKey(NavAction.NorthWest));
        Assert.Equal("swampward", NavKeyBindings.IniKey(NavAction.Swampward));
    }

    [Fact]
    public void AnAbsentOptionTakesItsDefault_AnEmptyOneHasNoKey_AnUnknownNameTakesTheDefault()
    {
        var ini = new IniFile();
        ini.Set(NavKeyBindings.Section, "north", "F4");
        ini.Set(NavKeyBindings.Section, "south", "");
        ini.Set(NavKeyBindings.Section, "east", "Enter");

        var read = NavKeyBindings.Read(ini.Get);

        Assert.Equal("F4", read.Get(NavAction.North));
        Assert.Equal("", read.Get(NavAction.South));
        Assert.Equal("Pad6", read.Get(NavAction.East));
        Assert.Equal("Pad4", read.Get(NavAction.West));
    }

    [Fact]
    public void WrittenBindingsReadBackIncludingOptionsWithNoKey()
    {
        var edited = NavKeyBindings.Default
            .With(NavAction.Scan, "")
            .With(NavAction.Down, "F9")
            .With(NavAction.Up, "PadDivide");
        var ini = new IniFile();

        edited.Write(ini.Set);
        var read = NavKeyBindings.Read(ini.Get);

        Assert.True(read.IsSameAs(edited));
        Assert.Equal("", read.Get(NavAction.Scan));   // stored as empty, not read back as Pad5
    }

    [Theory]
    [InlineData(KeyKind.Printable)]
    [InlineData(KeyKind.Enter)]
    [InlineData(KeyKind.Shift)]
    [InlineData(KeyKind.Control)]
    [InlineData(KeyKind.Backspace)]
    public void CollisionRefusesTheKeysTypingNeeds(KeyKind kind)
        => Assert.NotNull(KeyCollision.Check(new KeyRef(kind), _ => false));

    [Fact]
    public void CollisionRefusesAnFkeyWithAMacro_AndAllowsOneWithout()
    {
        bool HasValue(int n) => n == 2;

        Assert.Equal("F2 has a macro", KeyCollision.Check(new KeyRef(KeyKind.Function, 2), HasValue));
        Assert.Null(KeyCollision.Check(new KeyRef(KeyKind.Function, 3), HasValue));
    }

    [Fact]
    public void CollisionAllowsAnyOtherKey()
        => Assert.Null(KeyCollision.Check(new KeyRef(KeyKind.Other), _ => true));

    private sealed class Clock
    {
        public DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    [Fact]
    public void ScanIsSpacedByASecond()
    {
        var clock = new Clock();
        var gate = new NavScanGate(() => clock.Now);

        Assert.True(gate.TryScan());
        clock.Advance(0.5);
        Assert.False(gate.TryScan());
        clock.Advance(0.49);
        Assert.False(gate.TryScan());
        clock.Advance(0.02);
        Assert.True(gate.TryScan());
    }

    [Fact]
    public void ARefusedScanDoesNotRestartTheSecond()
    {
        var clock = new Clock();
        var gate = new NavScanGate(() => clock.Now);

        Assert.True(gate.TryScan());
        clock.Advance(0.9);
        Assert.False(gate.TryScan());
        clock.Advance(0.2);
        Assert.True(gate.TryScan());   // 1.1 s after the last one that went out
    }

    [Fact]
    public void AMoveReleasesTheNextScanOnce()
    {
        var clock = new Clock();
        var gate = new NavScanGate(() => clock.Now);

        Assert.True(gate.TryScan());
        gate.Moved();
        Assert.True(gate.TryScan());    // same instant: the room is new
        Assert.False(gate.TryScan());   // and the spacing applies again from that scan
    }
}
