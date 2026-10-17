# AGENTS.md — AdsiExplorer

Read-only Active Directory exploration over ADSI. Windows only. Two stacks must stay in command and filter parity.

| Stack | Entry | When to edit |
|-------|--------|----------------|
| PowerShell 5.1+ | `src/AdsiExplorer.ps1` | Shell commands, help, table output |
| PowerShell core | `src/AdsiCore.ps1` | Connect, search paging, LDAP escape, formatters |
| PowerShell extras | `src/AdsiCoreAdvanced.ps1` | Named queries, schema, members, tree, SID resolve |
| PowerShell shell extras | `src/AdsiShellAdvanced.ps1` | Query plan, count/next, profiles, aliases, history |
| .NET 8 | `dotnet/AdsiExplorer/` | The C# mirror of the same behavior |

User-facing usage lives in `README.md`. Do not duplicate it here.

## Change checklist

Touch both stacks for any user-visible command, flag, filter, or connect option.

1. PowerShell: command function plus the switch in `Invoke-AdsiShellCommand`, and `Show-AdsiShellHelp` when the command is new.
2. Query flags (`-filter`, `-query`, `-days`, `-props`, `-sort`, `-type`): `ConvertTo-AdsiQueryPlan` in `AdsiShellAdvanced.ps1` and `ParseQueryOptions` in `InteractiveShell.cs`.
3. C#: matching method and `switch` in `InteractiveShell.Run`, plus help text in `PrintHelp`.
4. LDAP filter logic: `AdsiCore.ps1` / `AdsiCoreAdvanced.ps1` and `LdapFilter.cs` / `AdsiNamedQueries.cs` / `AdsiDirectoryAdvanced.cs`.
5. `dotnet build .\dotnet\AdsiExplorer\AdsiExplorer.csproj -c Release`. A live DC smoke test is optional and only with credentials the user supplies at runtime.

Do not add AD writes (`CommitChanges`, `Put`, `DeleteTree`, set-password). Do not add `-password` to the .NET CLI.

## Where behavior lives (.NET)

| File | Role |
|------|------|
| `Program.cs` | CLI args. Password from prompt or `ADSI_PASSWORD`, then clear the process env var. |
| `AdsiSession.cs` | Bind. Credentials stay in `SecureString`. LDAP+user uses `AuthenticationTypes.Secure`; `-ssl` uses `SecureSocketsLayer`. Not both with `-gc`. |
| `AdsiSearchService.cs` | `Search` mirrors `Search-AdsiObjects`. |
| `LdapFilter.cs` | Escape, attribute-name check, SID and GUID filters. |
| `AdsiNamedQueries.cs` | Preset filters and UAC flag decode. |
| `AdsiDirectoryAdvanced.cs` | Schema, members, memberOf, tree, resolve. |
| `InteractiveShell.cs` | Commands, paging, tables. |
| `PropertyPresets.cs` / `AdsiPropertyFormatter.cs` | Property sets and display. |
| `ShellPersistence.cs` / `ShellState.cs` | `%APPDATA%\AdsiExplorer` aliases, profiles, history. |

PowerShell session fields: `Server`, `BaseDn`, `SearchEntry`, `Credential`, `UserName`, `UseSsl`, `UseGlobalCatalog`. No plaintext `Password` property.

Search result fields: `Filter`, `Results`, `Count`, `HasMore`, `Truncated` (same value as `HasMore`), `Skip`, `SortLimited`.

## Commands (keep both switches in sync)

`connect`, `status`, `list`, `search`, `show`, `props`, `limit`, `page`, `export`, `base`, `width`, `count`, `next`, `tree`, `members`, `memberof`, `resolve`, `schema`, `context`, `profile`, `alias`, `history`, `help`, `quit`/`exit`.

Object types: `users`, `groups`, `computers`, `ous`, `all`.

Named queries (`-query`): `disabled`, `locked`, `pwd-never-expires`, `spn`, `empty-groups`, `stale-computers` (`-days`). Matching-rule syntax such as `userAccountControl:1.2.840.113556.1.4.803:=2` belongs only in those named-query builders. User attribute names cannot contain `:`.

## LDAP rules

Build filters only through the helpers. Do not concatenate user text into a filter string.

- Values: `Escape-LdapFilterValue` / `LdapFilter.EscapeValue`. If the value contains `*`, split on `*`, escape each segment, rejoin with `*`.
- Attribute names: `Test-LdapFilterAttributeName` / `LdapFilter.ValidateAttributeName` (`^[\w.-]+$`) before the name is placed in the filter.
- `objectSid`: `^S-1(-\d+)+$`, then `SecurityIdentifier` binary form as `\xx` octets. `New-AdsiObjectSidFilter` / `LdapFilter.BuildObjectSidFilter`.
- `objectGUID`: parse the GUID, swap to AD byte order, emit `\xx`.
- `list -filter "(...)"` is a trusted raw filter. Leave it. Do not route it through the attribute validator.

```powershell
# Use this
New-AdsiSearchFilter -Attribute $attr -Value $value -ObjectType Users
New-AdsiObjectSidFilter -Sid $sid

# Not this
"(objectSid=$sid)"
"($attr=$value)"
```

## Shell behavior to preserve

- Paging is client-side: skip N results from `FindAll()`, then take `MaxResults`. `HasMore` is true only when another row exists past the page. Set `Truncated` to that same flag.
- `next` re-runs `LastQueryPlan` with `Skip + MaxResults`.
- `-sort` sorts the current page only. If `HasMore` or `Skip > 0`, set `SortLimited` and print the existing page-local note.
- Aliases: reject `` ` ``, `|`, `;`, and newlines on save and on expand (`Test-AdsiShellAliasCommand` / `ValidateAliasExpansion`).
- Tables: column width is max(header, cell). PowerShell `Format-AdsiShellResultTable`; C# `PrintTable`. Prefer `Write-Host` / `Console.WriteLine` so `-Commands` batch mode still shows output.
- PowerShell parameter names: use `$ShellArgs` and `-Commands`. `$Args` and `-Command` collide with built-ins.

## Secrets and git

- Never commit passwords, real lab hosts, or files from `%APPDATA%\AdsiExplorer`.
- README and usage examples stay generic (`dc01.contoso.com`, `CONTOSO\your.user`).
- Commit only when the user asks. `.gitignore` already drops `bin/`, `obj/`, `publish/`, and `*.exe`.
- Publish with `.\dotnet\publish.ps1`. If the exe is locked, the user must close the running `AdsiExplorer.exe` first. Do not kill it unless they ask.

## Auth note

`AuthenticationTypes.Secure` is Negotiate. A bind to a DC by IP from a non-domain machine can fail when NTLM is disabled. `-ssl` / `-UseSsl` is the supported workaround. Do not fall back to `AuthenticationTypes.None` for credentialed binds.
