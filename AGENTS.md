# AGENTS.md — AdsiExplorer

Guidance for AI agents and contributors working in this repository.

## What this project is

Read-only **Active Directory** exploration via **ADSI** (`System.DirectoryServices` / PowerShell ADSI). Two parallel implementations share the same interactive command vocabulary:

| Stack | Entry | Core logic |
|-------|--------|------------|
| PowerShell 5.1+ | `src/AdsiExplorer.ps1` | `AdsiCore.ps1`, `AdsiCoreAdvanced.ps1`, `AdsiShellAdvanced.ps1` |
| .NET 8 (Windows) | `dotnet/AdsiExplorer/Program.cs` | `InteractiveShell.cs`, `AdsiSearchService.cs`, `LdapFilter.cs`, `AdsiSession.cs`, etc. |

**Windows only.** No AD write operations.

## Before you change code

1. Read `README.md` for user-facing behavior and security notes.
2. Prefer **minimal diffs**; keep PowerShell and C# behavior aligned when touching search, connect, or shell commands.
3. **Never** commit credentials, lab IPs tied to real environments, or `%APPDATA%\AdsiExplorer` exports.
4. Do **not** add `-password` to the .NET CLI (use prompt or `ADSI_PASSWORD`, then clear from process env after read — see `Program.cs` / `InteractiveShell.cs`).

## Architecture (PowerShell)

```
AdsiExplorer.ps1          # param block, shell loop, command dispatch
  ├─ AdsiCore.ps1         # Escape-LdapFilterValue, Connect-AdsiDirectory, Search-AdsiObjects, formatters
  ├─ AdsiCoreAdvanced.ps1 # named queries, schema, members/memberOf, tree, SID resolve
  └─ AdsiShellAdvanced.ps1 # profiles/aliases/history, query plan, count/next, persistence under %APPDATA%
```

Session object: server, base DN, `SearchEntry`, `Credential` (SecureString), `UseSsl`, `UseGlobalCatalog` — **no plain-text password field**.

Search output: `Filter`, `Results`, `HasMore`, `Truncated` (same as `HasMore`), `Skip`, `SortLimited`.

## Architecture (.NET)

- `AdsiSession` — connect, naming contexts, `DirectoryEntry` with `Secure` (LDAP) or `SecureSocketsLayer` (LDAPS).
- `AdsiSearchService.Search` — mirrors `Search-AdsiObjects` paging/skip/hasMore.
- `LdapFilter` — escape values, validate attribute names (`^[\w.-]+$`), binary `objectSid` / AD `objectGUID` filters.
- `InteractiveShell` — command parity with PowerShell shell; `ShellPersistence` for aliases/profiles JSON.

Build:

```powershell
dotnet build .\dotnet\AdsiExplorer\AdsiExplorer.csproj -c Release
.\dotnet\publish.ps1   # optional publish; stop running AdsiExplorer.exe first if publish fails (file lock)
```

Smoke test (requires live DC and credentials):

```powershell
.\src\Invoke-AdsiCoreTest.ps1 -Server dc01.contoso.com -Username 'DOMAIN\user' -Password (Read-Host -AsSecureString)
```

## LDAP / security conventions

When building filters from user input:

- **Values**: `Escape-LdapFilterValue` / `LdapFilter.EscapeValue` (wildcards split then escape segments).
- **Attribute names**: `Test-LdapFilterAttributeName` / `LdapFilter.ValidateAttributeName` — do not inject raw attribute strings into filters.
- **objectSid**: validate `^S-1(-\d+)+$`, emit **binary** octet form in the filter (not `S-1-...` text).
- **objectGUID**: parse GUID, convert to AD byte order, hex `\xx` form.
- **Raw filters**: `list -filter "(...)"` is intentional escape hatch — document only; do not “fix” by blocking without user request.

Named queries with LDAP matching rules live in `Get-AdsiNamedQueryFilter` / `AdsiNamedQueries.cs`, not in user-supplied attribute names.

## Shell command parity

When adding or changing a command, update **both** `AdsiExplorer.ps1` dispatch and `InteractiveShell.cs` switch (and help text in both). Common commands: `connect`, `list`, `search`, `show`, `props`, `count`, `next`, `export`, `tree`, `members`, `memberof`, `resolve`, `schema`, `context`, `profile`, `alias`, `history`, `help`.

Paging: client-side skip over `FindAll()` enumeration; `next` re-runs last query plan with increased skip. `-sort` is **page-local** when `HasMore` or skip &gt; 0 — surface `SortLimited` note in output.

## UI / output

- Table columns: width = max(header, cells); use existing formatters (`Format-AdsiShellResultTable`, .NET `PrintTable`).
- Use `Write-Host` / `Console.WriteLine` for batch-visible output where the codebase already does.

## Git / releases

- Commit only when the user asks.
- `.gitignore` excludes `bin/`, `obj/`, publish output.
- Solution: `AdsiExplorer.sln` at repo root.

## Common pitfalls

- **Parity drift**: C# missing a flag (e.g. `-ssl`, `-query`, `-days`) that PS supports — check `ConvertTo-AdsiQueryPlan` / `ParseQueryOptions`.
- **Authentication**: `Secure` uses Negotiate; binding by IP to a non-domain machine may need LDAPS (`-ssl`).
- **Publishing**: locked `AdsiExplorer.exe` in `bin/.../publish/` breaks publish — user must close the process.
- **PowerShell**: avoid `$Args` and `-Command` as parameter names (automatic/builtin conflicts); project uses `$ShellArgs`, `-Commands`.
