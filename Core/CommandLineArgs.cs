namespace Mucka.Core;

/// <summary>
/// Parsed command-line arguments for mucka.
/// Usage:
///   mucka -profile &lt;name&gt;
///   mucka [-host &lt;host&gt;] [-port &lt;port&gt;] [-user &lt;name&gt;] [-account &lt;id&gt;] [-password &lt;pwd&gt;]
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

            // All remaining flags require a value argument.
            if (i + 1 >= args.Count)
                continue;

            var value = args[++i];

            switch (name)
            {
                case "profile":
                    result.Profile = value;
                    break;
                case "host":
                    result.Host = value;
                    break;
                case "port":
                    if (int.TryParse(value, out var port))
                        result.Port = port;
                    break;
                case "user":
                    result.User = value;
                    break;
                case "account":
                    result.Account = value;
                    break;
                case "password":
                    result.Password = value;
                    break;
            }
        }

        return result;
    }
}
