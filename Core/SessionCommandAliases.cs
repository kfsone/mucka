using System.Text.RegularExpressions;

namespace Mucka.Core;

/// <summary>Stores client commands whose lifetime is the current gameworld visit.</summary>
internal sealed class SessionCommandAliases
{
    private static readonly Regex AliasRefRegex = new(
        @"\$(\^[QWEqwe]|[A-Za-z][A-Za-z0-9_]*|[?<])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _versionExpansion;
    private readonly Dictionary<string, string> _commands =
        new(StringComparer.OrdinalIgnoreCase);

    public SessionCommandAliases(string version)
        => _versionExpansion = $"Mucka v{version}";

    public bool TryDefine(
        string definition,
        out string name,
        out string command,
        out string? error)
    {
        name = string.Empty;
        command = string.Empty;
        error = null;

        var equals = definition.IndexOf('=');
        if (equals < 0)
            return false;

        name = definition[..equals].Trim();
        command = definition[(equals + 1)..].Trim();

        if (!IsValidName(name))
        {
            error = "name must start with a letter and contain only letters, digits, or underscores";
            return true;
        }

        if (IsReservedClientCommand(name))
        {
            error = $"cannot replace built-in ${name}";
            return true;
        }

        if (command.Length == 0)
        {
            error = "command cannot be empty";
            return true;
        }

        foreach (Match match in AliasRefRegex.Matches(command))
        {
            var reference = match.Groups[1].Value;
            if (IsReservedClientCommand(reference) && reference != "VER")
            {
                error = $"cannot use built-in ${reference} in a command definition";
                return true;
            }
        }

        command = AliasRefRegex.Replace(command, match =>
        {
            var reference = match.Groups[1].Value;
            if (reference == "VER")
                return _versionExpansion;

            return _commands.TryGetValue(reference, out var expansion)
                ? expansion
                : match.Value;
        });

        _commands[name] = command;
        return true;
    }

    public bool TryGet(string name, out string command)
        => _commands.TryGetValue(name, out command!);

    public bool TryGetBuiltInExpansion(string name, out string expansion)
    {
        if (name == "VER")
        {
            expansion = _versionExpansion;
            return true;
        }

        expansion = string.Empty;
        return false;
    }

    public string Expand(string text)
        => AliasRefRegex.Replace(text, match =>
        {
            var reference = match.Groups[1].Value;
            if (reference == "VER")
                return _versionExpansion;

            return _commands.TryGetValue(reference, out var expansion)
                ? expansion
                : match.Value;
        });

    public void Clear() => _commands.Clear();

    private static bool IsValidName(string name)
    {
        if (name.Length == 2
            && name[0] == '^'
            && name[1] is 'Q' or 'q' or 'W' or 'w' or 'E' or 'e')
            return true;

        if (name.Length == 0 || !IsAsciiLetter(name[0]))
            return false;

        for (var i = 1; i < name.Length; i++)
        {
            var c = name[i];
            if (!IsAsciiLetter(c) && !char.IsAsciiDigit(c) && c != '_')
                return false;
        }

        return true;
    }

    private static bool IsAsciiLetter(char c)
        => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsReservedClientCommand(string name)
    {
        if (name is "help" or "?" or "<" or "con" or "map" or "fkeys" or "VER")
            return true;

        return name.Length >= 2
            && name[0] is 'f' or 'F'
            && int.TryParse(name[1..], out _);
    }
}
