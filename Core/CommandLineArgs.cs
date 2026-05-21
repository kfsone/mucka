namespace Mucka.Core;

/// <summary>
/// Parsed command-line arguments for mucka.
/// Usage:
///   mucka -profile &lt;name&gt;
///   mucka [-host &lt;host&gt;] [-port &lt;port&gt;] [-user &lt;name&gt;] [-account &lt;id&gt;] [-password &lt;pwd&gt;]
/// Warning:
///   -password exposes credentials via process listings, shell history, and crash reports.
///   Prefer a saved profile or the interactive password prompt when possible.
/// Debug builds only:
///   mucka [-record]
/// </summary>
public sealed class CommandLineArgs
{
    /// <summary>Name of a saved profile to load.</summary>
    public string? Profile { get; private set; }

    /// <summary>Override the host name.</summary>
    public string? Host { get; private set; }

    /// <summary>Override the port number.</summary>
    public int? Port { get; private set; }

    /// <summary>Override the telnet login user name.</summary>
    public string? User { get; private set; }

    /// <summary>Override the account ID.</summary>
    public string? Account { get; private set; }

    /// <summary>Override the password.</summary>
    public string? Password { get; private set; }

    /// <summary>Whether startup arguments request a direct connection instead of showing the profile page.</summary>
    public bool HasDirectConnectOptions =>
        Profile != null || Host != null || Port.HasValue || User != null || Account != null || Password != null;

#if DEBUG
    /// <summary>Arm session recording before connecting (debug builds only).</summary>
    public bool Record { get; private set; }
#endif

    private CommandLineArgs() { }

    /// <summary>
    /// Parsed arguments from <see cref="Environment.GetCommandLineArgs"/>, skipping the executable path.
    /// </summary>
    public static CommandLineArgs Current { get; } = Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());

    /// <summary>Parses the supplied argument list into a <see cref="CommandLineArgs"/> instance.</summary>
    public static CommandLineArgs Parse(IReadOnlyList<string> args)
    {
        var result = new CommandLineArgs();
        static bool IsRecognizedFlag(string arg)
        {
            if (!arg.StartsWith('-'))
                return false;

            return arg.TrimStart('-').ToLowerInvariant() switch
            {
                "profile" => true,
                "host" => true,
                "port" => true,
                "user" => true,
                "account" => true,
                "password" => true,
#if DEBUG
                "record" => true,
#endif
                _ => false
            };
        }

        for (int i = 0; i < args.Count; i++)
        {
            var flag = args[i];
            if (!flag.StartsWith('-'))
                continue;

            var name = flag.TrimStart('-').ToLowerInvariant();

#if DEBUG
            if (name == "record")
            {
                result.Record = true;
                continue;
            }
#endif

            bool TryTakeValue(out string value)
            {
                value = string.Empty;
                if (i + 1 >= args.Count || IsRecognizedFlag(args[i + 1]))
                    return false;

                value = args[++i];
                return true;
            }

            switch (name)
            {
                case "profile":
                    if (TryTakeValue(out var profile))
                        result.Profile = profile;
                    break;
                case "host":
                    if (TryTakeValue(out var host))
                        result.Host = host;
                    break;
                case "port":
                    if (TryTakeValue(out var portValue)
                        && int.TryParse(portValue, out var port)
                        && port is >= 1 and <= 65535)
                    {
                        result.Port = port;
                    }
                    break;
                case "user":
                    if (TryTakeValue(out var user))
                        result.User = user;
                    break;
                case "account":
                    if (TryTakeValue(out var account))
                        result.Account = account;
                    break;
                case "password":
                    if (TryTakeValue(out var password))
                        result.Password = password;
                    break;
            }
        }

        return result;
    }
}
