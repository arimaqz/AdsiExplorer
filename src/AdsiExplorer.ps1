#Requires -Version 5.1
param(
    [string]$Server,

    [string]$Username,

    [securestring]$Password,

    [string]$BaseDn,

    [switch]$UseGlobalCatalog,

    [switch]$UseSsl,

    [switch]$Quiet,

    [string[]]$Commands
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptDir 'AdsiCore.ps1')
. (Join-Path $scriptDir 'AdsiCoreAdvanced.ps1')
. (Join-Path $scriptDir 'AdsiShellAdvanced.ps1')

function Get-AdsiShellPropertyPresets {
    return @{
        minimal = @('distinguishedName', 'samAccountName', 'name', 'objectClass')
        user    = @(
            'distinguishedName', 'samAccountName', 'name', 'displayName', 'userPrincipalName',
            'mail', 'title', 'department', 'memberOf', 'lastLogonTimestamp', 'pwdLastSet',
            'userAccountControl', 'whenCreated', 'whenChanged'
        )
        group   = @(
            'distinguishedName', 'samAccountName', 'name', 'description', 'member',
            'memberOf', 'groupType', 'whenCreated', 'whenChanged'
        )
        computer = @(
            'distinguishedName', 'samAccountName', 'name', 'dnsHostName', 'operatingSystem',
            'operatingSystemVersion', 'lastLogonTimestamp', 'whenCreated', 'whenChanged'
        )
        ou      = @(
            'distinguishedName', 'name', 'description', 'whenCreated', 'whenChanged'
        )
        detail  = @(
            'distinguishedName', 'objectClass', 'objectCategory', 'samAccountName', 'name',
            'displayName', 'userPrincipalName', 'mail', 'memberOf', 'member', 'description',
            'userAccountControl', 'lastLogonTimestamp', 'pwdLastSet', 'whenCreated', 'whenChanged'
        )
    }
}

function Initialize-AdsiShellState {
    $presets = Get-AdsiShellPropertyPresets
    $script:AdsiShell = [ordered]@{
        Session          = $null
        LastSearch       = $null
        LastResults      = @()
        ObjectType       = 'Users'
        ListProperties   = [string[]]@($presets.minimal)
        DetailProperties = [string[]]@($presets.detail)
        MaxResults       = 100
        PageSize              = 1000
        PropertyPresets       = $presets
        LastDisplayProperties = [string[]]@($presets.minimal)
        TableCellWidth        = 48
        DefaultStaleDays      = 90
        LastQueryPlan         = $null
        QuerySkip             = 0
        Aliases               = (Get-AdsiShellAliases)
        History               = @()
        ExitCode              = 0
    }
}

function Resolve-AdsiShellPropertyList {
    param(
        [Parameter(Mandatory)]
        [string]$Spec
    )

    $presetKey = $Spec.ToLowerInvariant()
    if ($script:AdsiShell.PropertyPresets.Contains($presetKey)) {
        return [string[]]@($script:AdsiShell.PropertyPresets[$presetKey])
    }

    return [string[]]@($Spec -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Format-AdsiShellCellValue {
    param(
        $Value,

        [int]$MaxWidth
    )

    if ($null -eq $Value) {
        return ''
    }

    $text = if ($Value -is [System.Array]) {
        ($Value -join '; ')
    }
    else {
        [string]$Value
    }

    if ($MaxWidth -gt 0 -and $text.Length -gt $MaxWidth) {
        return $text.Substring(0, [Math]::Max(0, $MaxWidth - 3)) + '...'
    }

    return $text
}

function Split-AdsiShellLine {
    param(
        [AllowEmptyString()]
        [string]$Line
    )

    $tokens = New-Object System.Collections.Generic.List[string]
    if ([string]::IsNullOrWhiteSpace($Line)) {
        return @()
    }

    $current = New-Object System.Text.StringBuilder
    $inQuote = $false
    $quoteChar = '"'

    for ($i = 0; $i -lt $Line.Length; $i++) {
        $c = $Line[$i]
        if ($inQuote) {
            if ($c -eq $quoteChar) {
                $inQuote = $false
            }
            else {
                [void]$current.Append($c)
            }
            continue
        }

        if ($c -eq '"' -or $c -eq "'") {
            $inQuote = $true
            $quoteChar = $c
            continue
        }

        if ([char]::IsWhiteSpace($c)) {
            if ($current.Length -gt 0) {
                $tokens.Add($current.ToString())
                $current.Clear() | Out-Null
            }
            continue
        }

        [void]$current.Append($c)
    }

    if ($current.Length -gt 0) {
        $tokens.Add($current.ToString())
    }

    return @($tokens)
}

function Resolve-AdsiObjectTypeName {
    param(
        [string]$Name
    )

    if ([string]::IsNullOrWhiteSpace($Name)) {
        return $script:AdsiShell.ObjectType
    }

    switch ($Name.ToLowerInvariant()) {
        'user' { return 'Users' }
        'users' { return 'Users' }
        'group' { return 'Groups' }
        'groups' { return 'Groups' }
        'computer' { return 'Computers' }
        'computers' { return 'Computers' }
        'ou' { return 'OrganizationalUnits' }
        'ous' { return 'OrganizationalUnits' }
        'organizationalunit' { return 'OrganizationalUnits' }
        'organizationalunits' { return 'OrganizationalUnits' }
        'all' { return 'All' }
        default {
            throw "Unknown object type '$Name'. Use users, groups, computers, ous, or all."
        }
    }
}

function Assert-AdsiShellConnected {
    if (-not $script:AdsiShell.Session) {
        throw 'Not connected. Run: connect [server] [domain\user]'
    }
}

function Format-AdsiTableCell {
    param(
        [string]$Text,

        [int]$Width
    )

    if ($Text.Length -gt $Width) {
        $Text = $Text.Substring(0, $Width)
    }

    return $Text.PadRight($Width)
}

function Format-AdsiShellResultTable {
    param(
        [object[]]$Results,

        [string[]]$Properties
    )

    if (-not $Results -or $Results.Count -eq 0) {
        Write-Host 'No results.'
        return
    }

    $columns = if (@($Properties).Count -gt 0) { $Properties } else { $script:AdsiShell.LastDisplayProperties }
    $maxCell = $script:AdsiShell.TableCellWidth
    $headers = @('#') + @($columns)

    $rows = foreach ($item in $Results) {
        $cells = @([string]$item.Index)
        $disabled = Test-AdsiAccountDisabled -UserAccountControl $item.Object.userAccountControl
        foreach ($prop in $columns) {
            $val = Format-AdsiShellCellValue -Value $item.Object.$prop -MaxWidth $maxCell
            if ($disabled -and $prop -ieq 'samAccountName' -and $val) {
                $val = "[D] $val"
            }
            $cells += [string]$val
        }
        ,@($cells)
    }

    $widths = for ($i = 0; $i -lt $headers.Count; $i++) {
        $max = $headers[$i].Length
        foreach ($row in $rows) {
            $len = $row[$i].Length
            if ($len -gt $max) {
                $max = $len
            }
        }
        $max
    }

    $headerCells = for ($i = 0; $i -lt $headers.Count; $i++) {
        Format-AdsiTableCell -Text $headers[$i] -Width $widths[$i]
    }
    Write-Host ($headerCells -join ' | ')
    Write-Host (($widths | ForEach-Object { '-' * $_ }) -join '-+-')

    $rowIndex = 0
    foreach ($row in $rows) {
        $lineCells = for ($i = 0; $i -lt $row.Count; $i++) {
            Format-AdsiTableCell -Text $row[$i] -Width $widths[$i]
        }
        $line = $lineCells -join ' | '
        $disabled = $false
        if ($Results[$rowIndex].Object.userAccountControl) {
            $disabled = Test-AdsiAccountDisabled -UserAccountControl $Results[$rowIndex].Object.userAccountControl
        }
        if ($disabled) {
            Write-Host $line -ForegroundColor DarkGray
        }
        else {
            Write-Host $line
        }
        $rowIndex++
    }
}

function Set-AdsiShellLastResults {
    param(
        [Parameter(Mandatory)]
        $SearchOutput
    )

    $script:AdsiShell.LastSearch = $SearchOutput
    $index = 0
    $script:AdsiShell.LastResults = @(
        foreach ($obj in $SearchOutput.Results) {
            $index++
            [pscustomobject]@{
                Index  = $index
                Object = $obj
            }
        }
    )
}

function Invoke-AdsiShellConnect {
    param(
        [string[]]$ShellArgs
    )

    $server = $null
    $username = $null
    $baseDn = $null
    $useGc = $false
    $useSsl = $false
    $credential = $null

    for ($i = 0; $i -lt $ShellArgs.Count; $i++) {
        $token = $ShellArgs[$i]
        switch -Regex ($token) {
            '^-(?i)server$' {
                $server = $ShellArgs[++$i]
            }
            '^-(?i)user( name)?$' {
                $username = $ShellArgs[++$i]
            }
            '^-(?i)base(dn)?$' {
                $baseDn = $ShellArgs[++$i]
            }
            '^-(?i)gc$' {
                $useGc = $true
            }
            '^-(?i)ssl$' {
                $useSsl = $true
            }
            default {
                if (-not $server) {
                    $server = $token
                }
                elseif (-not $username) {
                    $username = $token
                }
                else {
                    throw "Unexpected connect argument: $token"
                }
            }
        }
    }

    if ($username) {
        $secure = Read-Host "Password for $username" -AsSecureString
        $credential = New-Object pscredential($username, $secure)
    }

    $session = Connect-AdsiDirectory -Server $server -BaseDn $baseDn -Credential $credential -UseGlobalCatalog:$useGc -UseSsl:$useSsl
    $script:AdsiShell.Session = $session
    $script:AdsiShell.LastSearch = $null
    $script:AdsiShell.LastResults = @()

    $target = if ($server) { $server } else { '(default domain)' }
    Write-Host "Connected to $target"
    Write-Host "  Base DN: $($session.BaseDn)"
    if ($session.UseGlobalCatalog) {
        Write-Host '  Global Catalog: yes'
    }
    if ($session.UseSsl) {
        Write-Host '  LDAPS: yes'
    }
}

function Invoke-AdsiShellList {
    param(
        [string[]]$ShellArgs
    )

    $plan = ConvertTo-AdsiQueryPlan -ShellArgs $ShellArgs
    Invoke-AdsiShellRunQuery -Plan $plan -Skip 0
}

function Invoke-AdsiShellSearch {
    param(
        [string[]]$ShellArgs
    )

    $plan = ConvertTo-AdsiQueryPlan -ShellArgs $ShellArgs -IsSearch
    Invoke-AdsiShellRunQuery -Plan $plan -Skip 0
}

function Get-AdsiShellObjectByReference {
    param(
        [Parameter(Mandatory)]
        [string]$Reference
    )

    Assert-AdsiShellConnected

    if ($Reference -match '^\d+$') {
        $idx = [int]$Reference
        $hit = $script:AdsiShell.LastResults | Where-Object { $_.Index -eq $idx } | Select-Object -First 1
        if (-not $hit) {
            throw "No result #$idx. Run list or search first."
        }
        return $hit.Object
    }

    $dn = $Reference
    $escapedDn = Escape-LdapFilterValue -Value $dn
    $filter = "(distinguishedName=$escapedDn)"
    $output = Search-AdsiObjects -Session $script:AdsiShell.Session `
        -ObjectType All `
        -Filter $filter `
        -Properties $script:AdsiShell.DetailProperties `
        -PageSize $script:AdsiShell.PageSize `
        -MaxResults 1

    if ($output.Count -eq 0) {
        throw "Object not found: $dn"
    }

    return $output.Results[0]
}

function Invoke-AdsiShellShow {
    param(
        [string[]]$ShellArgs
    )

    if ($ShellArgs.Count -lt 1) {
        throw 'Usage: show <#|distinguishedName> [-all]'
    }

    $showAll = $false
    $refTokens = New-Object System.Collections.Generic.List[string]
    foreach ($token in $ShellArgs) {
        if ($token -match '^-(?i)all$') {
            $showAll = $true
        }
        else {
            $refTokens.Add($token) | Out-Null
        }
    }

    if ($refTokens.Count -eq 0) {
        throw 'Usage: show <#|distinguishedName> [-all]'
    }

    $ref = ($refTokens -join ' ').Trim()
    $object = $null
    if ($ref -match '^\d+$') {
        $hit = $script:AdsiShell.LastResults | Where-Object { $_.Index -eq [int]$ref } | Select-Object -First 1
        if (-not $hit) {
            throw "No result #$ref. Run list or search first."
        }
        $object = $hit.Object
    }
    else {
        $object = Get-AdsiShellObjectByReference -Reference $ref
    }

    $dn = $object.distinguishedName
    if (-not $dn) {
        throw 'Could not resolve distinguishedName for show.'
    }

    if ($showAll) {
        $object = Get-AdsiAllObjectProperties -Session $script:AdsiShell.Session -DistinguishedName $dn
    }
    elseif ($ref -match '^\d+$') {
        $object = Get-AdsiShellObjectByReference -Reference $dn
    }

    $object | Format-List | Out-String | Write-Host
}

function Invoke-AdsiShellProps {
    param(
        [string[]]$ShellArgs
    )

    if ($ShellArgs.Count -eq 0) {
        Write-Host 'List properties:' ($script:AdsiShell.ListProperties -join ', ')
        Write-Host 'Detail properties:' ($script:AdsiShell.DetailProperties -join ', ')
        Write-Host ''
        Write-Host 'Presets: minimal, user, group, computer, ou, detail'
        Write-Host 'Usage:'
        Write-Host '  props list <preset|attr1,attr2,...>'
        Write-Host '  props detail <preset|attr1,attr2,...>'
        return
    }

    $target = $ShellArgs[0].ToLowerInvariant()
    $value = if ($ShellArgs.Count -gt 1) { ($ShellArgs[1..($ShellArgs.Count - 1)] -join ' ') } else { $null }

    if ($target -eq 'list' -or $target -eq 'detail') {
        if (-not $value) {
            throw "Usage: props $target <preset|attr1,attr2,...>"
        }

        $props = $null
        $presetKey = $value.ToLowerInvariant()
        if ($script:AdsiShell.PropertyPresets.Contains($presetKey)) {
            $props = @($script:AdsiShell.PropertyPresets[$presetKey])
        }
        else {
            $props = @($value -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        }

        if ($target -eq 'list') {
            $script:AdsiShell.ListProperties = $props
            Write-Host ('List properties set: ' + ($props -join ', '))
        }
        else {
            $script:AdsiShell.DetailProperties = $props
            Write-Host ('Detail properties set: ' + ($props -join ', '))
        }
        return
    }

    throw "Unknown props command. Use: props list|detail ..."
}

function Invoke-AdsiShellLimit {
    param(
        [string[]]$ShellArgs
    )

    if ($ShellArgs.Count -lt 1) {
        Write-Host "MaxResults: $($script:AdsiShell.MaxResults)"
        return
    }

    $n = [int]$ShellArgs[0]
    if ($n -lt 0) {
        throw 'MaxResults must be >= 0 (0 = no limit).'
    }
    $script:AdsiShell.MaxResults = $n
    Write-Host "MaxResults set to $n"
}

function Invoke-AdsiShellPage {
    param(
        [string[]]$ShellArgs
    )

    if ($ShellArgs.Count -lt 1) {
        Write-Host "PageSize: $($script:AdsiShell.PageSize)"
        return
    }

    $n = [int]$ShellArgs[0]
    if ($n -lt 1) {
        throw 'PageSize must be >= 1.'
    }
    $script:AdsiShell.PageSize = $n
    Write-Host "PageSize set to $n"
}

function Invoke-AdsiShellExport {
    param(
        [string[]]$ShellArgs
    )

    if ($script:AdsiShell.LastResults.Count -eq 0) {
        throw 'Nothing to export. Run list or search first.'
    }

    $useDetail = $false
    $path = $null
    for ($i = 0; $i -lt $ShellArgs.Count; $i++) {
        $token = $ShellArgs[$i]
        if ($token -match '^-(?i)detail$') {
            $useDetail = $true
        }
        elseif (-not $path) {
            $path = $token
        }
        else {
            throw "Unexpected export argument: $token"
        }
    }

    if (-not $path) {
        throw 'Usage: export <file.json|file.csv> [-detail]'
    }

    $rows = foreach ($hit in $script:AdsiShell.LastResults) {
        $obj = $hit.Object
        if ($useDetail -and $obj.distinguishedName) {
            $obj = Get-AdsiShellObjectByReference -Reference $obj.distinguishedName
        }
        $flat = [ordered]@{ Index = $hit.Index }
        foreach ($prop in $obj.PSObject.Properties.Name) {
            $val = $obj.$prop
            if ($val -is [System.Array]) {
                $flat[$prop] = ($val -join '; ')
            }
            else {
                $flat[$prop] = $val
            }
        }
        [pscustomobject]$flat
    }

    if ($path -match '\.json$') {
        $rows | ConvertTo-Json -Depth 6 | Set-Content -Path $path -Encoding UTF8
    }
    elseif ($path -match '\.csv$') {
        $rows | Export-Csv -Path $path -NoTypeInformation -Encoding UTF8
    }
    else {
        throw 'Export path must end with .json or .csv'
    }

    Write-Host "Exported $($rows.Count) row(s) to $path"
}

function Invoke-AdsiShellBase {
    param(
        [string[]]$ShellArgs
    )

    Assert-AdsiShellConnected

    if ($ShellArgs.Count -lt 1) {
        Write-Host "Search base: $($script:AdsiShell.Session.SearchBaseDn)"
        return
    }

    $target = $ShellArgs[0]
    if ($target -match '^(?i)reset$') {
        Set-AdsiSearchBase -Session $script:AdsiShell.Session -SearchBaseDn $script:AdsiShell.Session.BaseDn | Out-Null
        Write-Host "Search base reset to $($script:AdsiShell.Session.BaseDn)"
        return
    }

    $searchBase = ($ShellArgs -join ' ').Trim()
    Set-AdsiSearchBase -Session $script:AdsiShell.Session -SearchBaseDn $searchBase | Out-Null
    Write-Host "Search base set to $searchBase"
}

function Invoke-AdsiShellWidth {
    param(
        [string[]]$ShellArgs
    )

    if ($ShellArgs.Count -lt 1) {
        Write-Host "Table cell width: $($script:AdsiShell.TableCellWidth)"
        return
    }

    $n = [int]$ShellArgs[0]
    if ($n -lt 0) {
        throw 'Width must be >= 0 (0 = no truncation).'
    }
    $script:AdsiShell.TableCellWidth = $n
    Write-Host "Table cell width set to $n"
}

function Invoke-AdsiShellStatus {
    $session = $script:AdsiShell.Session
    if (-not $session) {
        Write-Host 'Not connected.'
        return
    }

    Write-Host "Server: $(if ($session.Server) { $session.Server } else { '(default)' })"
    Write-Host "Base DN: $($session.BaseDn)"
    Write-Host "Search base: $($session.SearchBaseDn)"
    Write-Host "Global Catalog: $($session.UseGlobalCatalog)"
    Write-Host "Object type: $($script:AdsiShell.ObjectType)"
    Write-Host "MaxResults: $($script:AdsiShell.MaxResults)"
    Write-Host "PageSize: $($script:AdsiShell.PageSize)"
    Write-Host "Last result count: $($script:AdsiShell.LastResults.Count)"
    if ($script:AdsiShell.LastSearch) {
        Write-Host "Last filter: $($script:AdsiShell.LastSearch.Filter)"
    }
}

function Show-AdsiShellHelp {
    @'
Commands:
  connect [server] [domain\user]   Connect (prompts for password if user set)
  connect -server X -user Y -gc -ssl  Named connect options
  status                           Show session settings
  list [users|groups|...]          List objects (current type)
  list -filter "(...)"             List with custom LDAP filter
  list -type users                 Override object type for this query
  list users -props user           One-off property set for this query
  list -props samAccountName,mail  Custom columns for one query
  search <attr> <value>            Search by attribute (supports * wildcards)
  search samAccountName neo -type users -props user
  show <#>                         Show details for last result index
  show 1 -all                      All attributes via LDAP object read
  show "CN=name,DC=..."            Show object by distinguished name
  base [dn|reset]                  Scope list/search to an OU/container
  width [chars]                    Truncate table cells (0 = full width)
  props                            Show property configuration
  props list user                  Set list columns from preset
  props detail user                Set detail fields from preset
  props list dn,samAccountName,... Custom list columns
  limit [n]                        Max rows returned (0 = unlimited)
  page [n]                         LDAP page size
  export out.json|out.csv          Export last result set
  export out.csv -detail           Export with detail property reload
  count [type] [-query name]       Count matches without listing rows
  next                             Next page of last list/search
  tree                             OU tree under current search base
  members <#|dn> [-recursive]      Expand group members
  memberof <#|dn> [-recursive]     List group membership
  resolve <sid>                    Resolve SID to account name
  schema user                      List schema attributes for a class
  context domain|config|schema     Switch naming context search base
  profile save|load|list             Connection profiles (password prompted)
  alias name cmd... | alias list   Saved command shortcuts
  history [n]                        Recent commands
  help [list|search]                 Help topics
  quit | exit                      Exit

Named queries (-query): disabled, locked, pwd-never-expires, spn, empty-groups, stale-computers (-days 90)
Presets: minimal, user, group, computer, ou, detail
'@ | Write-Host
}

function Invoke-AdsiShellCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Line
    )

    $Line = Expand-AdsiShellAlias -Line $Line
    Add-AdsiShellHistory -Line $Line

    $tokens = @(Split-AdsiShellLine -Line $Line)
    if ($tokens.Count -eq 0) {
        return $true
    }

    $verb = $tokens[0].ToLowerInvariant()
    $shellArgs = @()
    if ($tokens.Count -gt 1) {
        $shellArgs = @($tokens[1..($tokens.Count - 1)])
    }

    switch ($verb) {
        'connect' { Invoke-AdsiShellConnect -ShellArgs $shellArgs }
        'status' { Invoke-AdsiShellStatus }
        'list' { Invoke-AdsiShellList -ShellArgs $shellArgs }
        'search' { Invoke-AdsiShellSearch -ShellArgs $shellArgs }
        'show' { Invoke-AdsiShellShow -ShellArgs $shellArgs }
        'props' { Invoke-AdsiShellProps -ShellArgs $shellArgs }
        'limit' { Invoke-AdsiShellLimit -ShellArgs $shellArgs }
        'page' { Invoke-AdsiShellPage -ShellArgs $shellArgs }
        'export' { Invoke-AdsiShellExport -ShellArgs $shellArgs }
        'base' { Invoke-AdsiShellBase -ShellArgs $shellArgs }
        'width' { Invoke-AdsiShellWidth -ShellArgs $shellArgs }
        'count' { Invoke-AdsiShellCount -ShellArgs $shellArgs }
        'next' { Invoke-AdsiShellNext }
        'tree' { Invoke-AdsiShellTree }
        'members' { Invoke-AdsiShellMembers -ShellArgs $shellArgs }
        'memberof' { Invoke-AdsiShellMemberOf -ShellArgs $shellArgs }
        'resolve' { Invoke-AdsiShellResolve -ShellArgs $shellArgs }
        'schema' { Invoke-AdsiShellSchema -ShellArgs $shellArgs }
        'context' { Invoke-AdsiShellContext -ShellArgs $shellArgs }
        'profile' { Invoke-AdsiShellProfile -ShellArgs $shellArgs }
        'alias' { Invoke-AdsiShellAlias -ShellArgs $shellArgs }
        'history' { Invoke-AdsiShellHistory -ShellArgs $shellArgs }
        'help' {
            if ($shellArgs.Count -gt 0) { Show-AdsiCommandHelp -Topic $shellArgs[0] }
            else { Show-AdsiShellHelp }
        }
        '?' {
            if ($shellArgs.Count -gt 0) { Show-AdsiCommandHelp -Topic $shellArgs[0] }
            else { Show-AdsiShellHelp }
        }
        'quit' { return $false }
        'exit' { return $false }
        default { throw "Unknown command: $($tokens[0]). Type help." }
    }

    return $true
}

function Start-AdsiExplorer {
    Initialize-AdsiShellState

    if (Get-Module -ListAvailable -Name PSReadLine) {
        Import-Module PSReadLine -ErrorAction SilentlyContinue | Out-Null
    }

    if ($Server -or $Username -or $Password -or $BaseDn -or $UseGlobalCatalog -or $UseSsl) {
        $credential = $null
        if ($Username) {
            if (-not $Password) {
                $Password = Read-Host "Password for $Username" -AsSecureString
            }
            $credential = New-Object pscredential($Username, $Password)
        }

        $script:AdsiShell.Session = Connect-AdsiDirectory `
            -Server $Server `
            -BaseDn $BaseDn `
            -Credential $credential `
            -UseGlobalCatalog:$UseGlobalCatalog `
            -UseSsl:$UseSsl

        if (-not $Quiet) {
            $target = if ($Server) { $Server } else { '(default domain)' }
            Write-Host "Connected to $target (Base DN: $($script:AdsiShell.Session.BaseDn))"
        }
    }

    if (-not ($Commands -and $Commands.Count -gt 0)) {
        Show-AdsiShellHelp
    }

    if ($Commands -and $Commands.Count -gt 0) {
        foreach ($line in $Commands) {
            Write-Host "adsi> $line"
            try {
                if (-not (Invoke-AdsiShellCommand -Line $line)) {
                    exit $script:AdsiShell.ExitCode
                }
            }
            catch {
                Write-Warning $_.Exception.Message
                $script:AdsiShell.ExitCode = 1
            }
        }
        exit $script:AdsiShell.ExitCode
    }

    while ($true) {
        $line = Read-Host 'adsi>'
        try {
            if (-not (Invoke-AdsiShellCommand -Line $line)) {
                break
            }
        }
        catch {
            Write-Warning $_.Exception.Message
        }
    }
}

Start-AdsiExplorer
