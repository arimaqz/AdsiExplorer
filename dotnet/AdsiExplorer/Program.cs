namespace AdsiExplorer;

internal static class Program
{
    private static int Main(string[] args)
    {
        string? server = null;
        string? userName = null;
        string? password = null;
        string? baseDn = null;
        var useGlobalCatalog = false;
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
                case "-password":
                    password = args[++i];
                    break;
                case "-basedn":
                case "-base":
                    baseDn = args[++i];
                    break;
                case "-gc":
                    useGlobalCatalog = true;
                    break;
                case "-quiet":
                    quiet = true;
                    break;
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

        if (userName is not null && password is null && !Console.IsInputRedirected)
        {
            Console.Write($"Password for {userName}: ");
            password = ReadPassword();
        }

        var shell = new InteractiveShell(quiet);
        if (server is not null || userName is not null || password is not null || baseDn is not null || useGlobalCatalog)
        {
            shell.ConnectStartup(server, userName, password, baseDn, useGlobalCatalog);
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
  AdsiExplorer [-server HOST] [-user DOMAIN\\user] [-password PASS] [-base DN] [-gc] [-quiet]
               [-commands "list users" "search samAccountName neo"]

Examples:
  AdsiExplorer -server 192.168.10.10 -user matrix\\administrator
  AdsiExplorer -server dc01.matrix.local -user matrix\\administrator -commands "list users" "show 1"
""");
    }
}
