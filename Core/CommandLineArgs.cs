namespace Mucka.Core;

/// <summary>
/// Parsed command-line arguments for mucka.
/// Usage:
///   mucka -profile &lt;name&gt;
///   mucka [-host &lt;host&gt;] [-port &lt;port&gt;] [-user &lt;name&gt;] [-account &lt;id&gt;] [-password &lt;pwd&gt;]
/// Warning:
///   -password exposes credentials via process listings, shell history, and crash reports.
///   Prefer a saved profile or the interactive password prompt when possible.
/// Windows only:
///   mucka -logs &lt;path&gt;   — write trace/log output to the specified file (appended, auto-flushed)
/// Debug builds only:
///   mucka [-record]
/// </summary>
public sealed class CommandLineArgs
{
    /// <summary>Error encountered while parsing command-line arguments.</summary>
    public string? Error { get; private set; }

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

#if WINDOWS
    /// <summary>If set, trace/log output is written to this file (Windows only).</summary>
    public string? LogPath { get; private set; }
#endif

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
#if WINDOWS
                "logs" => true,
#endif
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

            string? TakeValue()
            {
                if (i + 1 >= args.Count || IsRecognizedFlag(args[i + 1]))
                {
                    result.Error = $"Missing value for command-line option: {flag}";
                    return null;
                }

                return args[++i];
            }

            switch (name)
            {
                case "profile":
                    if (TakeValue() is { } profile)
                        result.Profile = profile;
                    break;
                case "host":
                    if (TakeValue() is { } host)
                        result.Host = host;
                    break;
                case "port":
                    if (TakeValue() is { } portValue
                        && int.TryParse(portValue, out var port)
                        && port is >= 1 and <= 65535)
                    {
                        result.Port = port;
                    }
                    break;
                case "user":
                    if (TakeValue() is { } user)
                        result.User = user;
                    break;
                case "account":
                    if (TakeValue() is { } account)
                        result.Account = account;
                    break;
                case "password":
                    if (TakeValue() is { } password)
                        result.Password = password;
                    break;
#if WINDOWS
                case "logs":
                    if (TakeValue() is { } logPath)
                        result.LogPath = logPath;
                    break;
#endif
                default:
                    result.Error = $"Unknown command-line option: {flag}";
                    break;
            }

            if (result.Error != null)
            {
                return result;
            }
        }

        return result;
    }
}
