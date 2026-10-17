# AdsiExplorer

Interactive Active Directory enumeration using **ADSI** (`DirectorySearcher` / `[adsisearcher]`). List and search LDAP objects, inspect properties, export results, and run common inventory queries (disabled users, stale computers, group membership, and more).

## Requirements

- **Windows** (ADSI is Windows-only)
- **PowerShell 5.1+** for the script entry point
- **.NET 8 SDK** (optional) to build the standalone CLI

Domain membership or explicit credentials with LDAP read access to the target directory.

## Quick start (PowerShell)

```powershell
git clone <your-repo-url>
cd adsi

# Interactive shell
.\src\AdsiExplorer.ps1 -Server dc01.contoso.com -Username 'CONTOSO\your.user'

# One-shot commands
.\src\AdsiExplorer.ps1 -Server dc01.contoso.com -Username 'CONTOSO\your.user' `
  -Commands @('list users -props user', 'count users -query disabled')
```

Passwords are prompted at runtime (or `-Password` as `SecureString` in PowerShell only). They are **not** stored in repo files or saved profiles (profiles only store server, username, and base DN). The .NET CLI does **not** accept `-password` on the command line; use the prompt or the `ADSI_PASSWORD` environment variable for automation (clear it afterward on shared systems).

## Quick start (.NET CLI)

```powershell
dotnet publish .\dotnet\AdsiExplorer\AdsiExplorer.csproj -c Release -r win-x64 --self-contained false

.\dotnet\AdsiExplorer\bin\Release\net8.0\win-x64\publish\AdsiExplorer.exe `
  -server dc01.contoso.com -user CONTOSO\your.user
```

Or use the helper script:

```powershell
.\dotnet\publish.ps1
# Self-contained single file (large download):
.\dotnet\publish.ps1 -SelfContained
```

## Project layout

```
src/
  AdsiCore.ps1            # LDAP search, formatting, connection
  AdsiCoreAdvanced.ps1    # Named queries, schema, groups, tree
  AdsiShellAdvanced.ps1   # Profiles, aliases, history, paging
  AdsiExplorer.ps1        # Interactive PowerShell CLI
  Invoke-AdsiCoreTest.ps1 # Minimal connectivity smoke test
dotnet/
  AdsiExplorer/           # C# port of the interactive CLI
  publish.ps1
```

## Example commands

```text
connect dc01.contoso.com contoso\administrator
list users -query disabled -props user -sort samAccountName
search anr jdoe -props user
show 1 -all
members 2 -recursive
tree
context schema
schema user
count computers -query stale-computers -days 90
next
export results.csv -detail
profile save lab
alias disabled list users -query disabled
help search
```

Named queries (`-query`): `disabled`, `locked`, `pwd-never-expires`, `spn`, `empty-groups`, `stale-computers` (use `-days N` for stale computers).

## Smoke test

```powershell
.\src\Invoke-AdsiCoreTest.ps1 -Server dc01.contoso.com -Username 'CONTOSO\your.user' `
  -Password (Read-Host -AsSecureString)
```

## Security notes

- Read-only by design (no AD write operations).
- Default bind is **LDAP** (port 389) with `AuthenticationTypes.Secure` (signed Kerberos/NTLM) when credentials are supplied. Use `-UseSsl` / `connect ... -ssl` / `-ssl` for **LDAPS** (`SecureSocketsLayer`). Plain LDAP without credentials uses the process identity.
- `search objectSid` / `resolve` use the binary SID form expected by Active Directory.
- `-sort` orders only the current page when paging or `next` is in use.
- Search filters built from user input (SIDs, schema class names, attribute values) are escaped or validated to reduce LDAP filter injection risk. Raw `list -filter "(...)"` still accepts full LDAP filter syntax — use only with trusted input.
- Avoid committing credentials, exports with live directory data, or `%APPDATA%\AdsiExplorer` profile files if they contain sensitive hostnames you do not want public.
- Run with least-privilege accounts where possible.

## License

MIT — see [LICENSE](LICENSE).
