using System.Security;

namespace AdsiExplorer;

internal static class Program
{
    private static int Main(string[] args)
    {
        string? server = null;
        string? userName = null;
        string? baseDn = null;
        var useGlobalCatalog = false;
        var useSsl = false;
        var quiet = false;
        var commands = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "-server":
                    server = args[++i];
                    break;
                case "-user":
                case "-username":
                    userName = args[++i];
                    break;
                case "-basedn":
                case "-base":
                    baseDn = args[++i];
                    break;
                case "-gc":
                    useGlobalCatalog = true;
                    break;
                case "-ssl":
                    useSsl = true;
                    break;
                case "-quiet":
                    quiet = true;
                    break;
                case "-password":
                    Console.Error.WriteLine("Warning: -password is not supported (visible in process list). Use prompt or ADSI_PASSWORD.");
                    return 2;
                case "-commands":
                    while (++i < args.Length)
                    {
                        commands.Add(args[i]);
                    }
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument: {arg}");
                    PrintUsage();
                    return 2;
            }
        }

        SecureString? passwordSecure = null;
        if (userName is not null)
        {
            var fromEnv = Environment.GetEnvironmentVariable("ADSI_PASSWORD");
            if (!string.IsNullOrEmpty(fromEnv))
            {
                passwordSecure = AdsiSession.ToSecureString(fromEnv);
                Environment.SetEnvironmentVariable("ADSI_PASSWORD", null, EnvironmentVariableTarget.Process);
            }
            else if (!Console.IsInputRedirected)
            {
                Console.Write($"Password for {userName}: ");
                passwordSecure = AdsiSession.ToSecureString(ReadPassword());
            }
        }

        var shell = new InteractiveShell(quiet);
        if (server is not null || userName is not null || passwordSecure is not null || baseDn is not null || useGlobalCatalog || useSsl)
        {
            shell.ConnectStartup(server, userName, passwordSecure, baseDn, useGlobalCatalog, useSsl);
        }

        shell.Run(commands.Count > 0 ? commands : null);
        return shell.ExitCode;
    }

    private static string ReadPassword()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                }

                continue;
            }

            sb.Append(key.KeyChar);
        }

        return sb.ToString();
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
Usage:
  AdsiExplorer [-server HOST] [-user DOMAIN\\user] [-base DN] [-gc] [-ssl] [-quiet]
               [-commands "list users" "search samAccountName neo"]

Password: prompted at runtime, or set ADSI_PASSWORD (avoid on shared machines; clears only from env if you unset it).

Examples:
  AdsiExplorer -server dc01.contoso.com -user CONTOSO\\your.user
  AdsiExplorer -server dc01.contoso.com -user CONTOSO\\your.user -ssl -commands "list users" "show 1"
""");
    }
}
