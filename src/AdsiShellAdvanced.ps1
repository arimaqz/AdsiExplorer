#Requires -Version 5.1
Set-StrictMode -Version Latest

function Get-AdsiShellDataDirectory {
    $path = Join-Path $env:APPDATA 'AdsiExplorer'
    if (-not (Test-Path $path)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }
    return $path
}

function ConvertFrom-AdsiJsonMap {
    param([string]$Json)
    $obj = $Json | ConvertFrom-Json
    $map = @{}
    foreach ($prop in $obj.PSObject.Properties) {
        $map[$prop.Name] = $prop.Value
    }
    return $map
}

function Get-AdsiShellProfiles {
    $file = Join-Path (Get-AdsiShellDataDirectory) 'profiles.json'
    if (-not (Test-Path $file)) {
        return @{}
    }
    return (ConvertFrom-AdsiJsonMap -Json (Get-Content $file -Raw))
}

function Save-AdsiShellProfiles {
    param([hashtable]$Profiles)
    $file = Join-Path (Get-AdsiShellDataDirectory) 'profiles.json'
    ($Profiles | ConvertTo-Json -Depth 4) | Set-Content -Path $file -Encoding UTF8
}

function Get-AdsiShellAliases {
    $file = Join-Path (Get-AdsiShellDataDirectory) 'aliases.json'
    if (-not (Test-Path $file)) {
        return @{
            disabled = 'list users -query disabled'
            spn      = 'list users -query spn'
        }
    }
    return (ConvertFrom-AdsiJsonMap -Json (Get-Content $file -Raw))
}

function Test-AdsiShellAliasCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Command
    )

    if ($Command -match '[\r\n;|`]' -or $Command -match '\|\|') {
        throw 'Alias blocked: command contains disallowed characters.'
    }
}

function Save-AdsiShellAliases {
    param([hashtable]$Aliases)
    $file = Join-Path (Get-AdsiShellDataDirectory) 'aliases.json'
    ($Aliases | ConvertTo-Json -Depth 3) | Set-Content -Path $file -Encoding UTF8
}

function Add-AdsiShellHistory {
    param([string]$Line)
    if ([string]::IsNullOrWhiteSpace($Line)) { return }
    $script:AdsiShell.History += $Line
    if ($script:AdsiShell.History.Count -gt 500) {
        $script:AdsiShell.History = @($script:AdsiShell.History | Select-Object -Last 500)
    }
    $file = Join-Path (Get-AdsiShellDataDirectory) 'history.txt'
    Add-Content -Path $file -Value $Line -Encoding UTF8
}

function Expand-AdsiShellAlias {
    param([string]$Line)
    $tokens = @(Split-AdsiShellLine -Line $Line)
    if ($tokens.Count -eq 0) { return $Line }
    $aliases = $script:AdsiShell.Aliases
    if ($aliases.Contains($tokens[0].ToLowerInvariant())) {
        $expansion = [string]$aliases[$tokens[0].ToLowerInvariant()]
        Test-AdsiShellAliasCommand -Command $expansion
        $rest = if ($tokens.Count -gt 1) { ' ' + ($tokens[1..($tokens.Count - 1)] -join ' ') } else { '' }
        return ($expansion + $rest)
    }
    return $Line
}

function ConvertTo-AdsiQueryPlan {
    param(
        [string[]]$ShellArgs,
        [switch]$IsSearch,
        [switch]$IsCount
    )

    $objectType = $script:AdsiShell.ObjectType
    $filter = $null
    $propsSpec = $null
    $attribute = $null
    $value = $null
    $queryName = $null
    $sortProperty = $null
    $staleDays = $script:AdsiShell.DefaultStaleDays
    $index = 0

    if ($IsSearch) {
        if ($ShellArgs.Count -lt 2) {
            throw 'Usage: search <attribute> <value> [-type ...] [-props ...] [-sort attr]'
        }
        $attribute = $ShellArgs[0]
        $value = $ShellArgs[1]
        $index = 2
    }

    while ($index -lt $ShellArgs.Count) {
        $token = $ShellArgs[$index]
        if ($token -match '^-(?i)filter$') { $filter = $ShellArgs[++$index] }
        elseif ($token -match '^-(?i)type$') { $objectType = Resolve-AdsiObjectTypeName -Name $ShellArgs[++$index] }
        elseif ($token -match '^-(?i)props$') { $propsSpec = $ShellArgs[++$index] }
        elseif ($token -match '^-(?i)query$') { $queryName = $ShellArgs[++$index] }
        elseif ($token -match '^-(?i)sort$') { $sortProperty = $ShellArgs[++$index] }
        elseif ($token -match '^-(?i)days$') { $staleDays = [int]$ShellArgs[++$index] }
        elseif (-not $IsSearch) { $objectType = Resolve-AdsiObjectTypeName -Name $token }
        else { throw "Unexpected argument: $token" }
        $index++
    }

    if ($queryName) {
        $filter = Get-AdsiNamedQueryFilter -Name $queryName -StaleDays $staleDays
    }

    $propertyList = if ($propsSpec) {
        Resolve-AdsiShellPropertyList -Spec $propsSpec
    }
    else {
        [string[]]@($script:AdsiShell.ListProperties)
    }

    if ($attribute) {
        $attrLower = $attribute.ToLowerInvariant()
        if ($attrLower -in @('anr', 'objectsid', 'objectguid')) {
            $filter = New-AdsiIdentitySearchFilter -Attribute $attribute -Value $value -ObjectType $objectType
            $attribute = $null
            $value = $null
        }
    }

    return [pscustomobject]@{
        ObjectType   = $objectType
        Filter       = $filter
        Attribute    = $attribute
        Value        = $value
        PropertyList = $propertyList
        SortProperty = $sortProperty
        StaleDays    = $staleDays
        IsCount      = [bool]$IsCount
    }
}

function Invoke-AdsiShellRunQuery {
    param(
        [Parameter(Mandatory)]
        $Plan,

        [int]$Skip = 0
    )

    Assert-AdsiShellConnected
    $script:AdsiShell.ObjectType = $Plan.ObjectType
    $script:AdsiShell.LastDisplayProperties = $Plan.PropertyList
    $script:AdsiShell.LastQueryPlan = $Plan
    $script:AdsiShell.QuerySkip = $Skip

    $output = Search-AdsiObjects -Session $script:AdsiShell.Session `
        -ObjectType $Plan.ObjectType `
        -Filter $Plan.Filter `
        -Attribute $Plan.Attribute `
        -Value $Plan.Value `
        -Properties $Plan.PropertyList `
        -PageSize $script:AdsiShell.PageSize `
        -MaxResults $(if ($Plan.IsCount) { 0 } else { $script:AdsiShell.MaxResults }) `
        -Skip $Skip `
        -CountOnly:$Plan.IsCount `
        -SortProperty $Plan.SortProperty

    if ($Plan.IsCount) {
        Write-Host "Filter: $($output.Filter)"
        Write-Host "Count: $($output.Count)"
        return
    }

    Set-AdsiShellLastResults -SearchOutput $output
    Write-Host "Filter: $($output.Filter)"
    Write-Host "Search base: $($script:AdsiShell.Session.SearchBaseDn)"
    $pageInfo = if ($Skip -gt 0) { " (skip $Skip)" } else { '' }
    Write-Host "Returned $($output.Count) object(s)$pageInfo$(if ($output.Truncated) { ' (page full)' })$(if ($output.HasMore) { ' — type next for more' })."
    if ($output.SortLimited) {
        Write-Host 'Note: -sort applies to the current page only, not the full result set.'
    }
    Format-AdsiShellResultTable -Results $script:AdsiShell.LastResults -Properties $Plan.PropertyList
}

function Invoke-AdsiShellNext {
    if (-not $script:AdsiShell.LastQueryPlan) {
        throw 'No previous query. Run list or search first.'
    }
    if (-not $script:AdsiShell.LastSearch -or -not $script:AdsiShell.LastSearch.HasMore) {
        throw 'No more results for the last query.'
    }
    $skip = $script:AdsiShell.QuerySkip + $script:AdsiShell.MaxResults
    Invoke-AdsiShellRunQuery -Plan $script:AdsiShell.LastQueryPlan -Skip $skip
}

function Invoke-AdsiShellCount {
    param([string[]]$ShellArgs)
    $plan = ConvertTo-AdsiQueryPlan -ShellArgs $ShellArgs -IsCount
    Invoke-AdsiShellRunQuery -Plan $plan -Skip 0
}

function Invoke-AdsiShellTree {
    Assert-AdsiShellConnected
    $lines = Get-AdsiOuTree -Session $script:AdsiShell.Session
    if ($lines.Count -eq 0) {
        Write-Host 'No OUs under current search base.'
        return
    }
    $lines | ForEach-Object { Write-Host $_ }
}

function Invoke-AdsiShellMembers {
    param([string[]]$ShellArgs)
    Assert-AdsiShellConnected
    if ($ShellArgs.Count -lt 1) { throw 'Usage: members <#|dn> [-recursive]' }
    $recursive = $ShellArgs -contains '-recursive'
    $ref = ($ShellArgs | Where-Object { $_ -notmatch '^-(?i)recursive$' }) -join ' '
    $dn = Resolve-AdsiShellReferenceDn -Reference $ref
    $members = Get-AdsiGroupMembers -Session $script:AdsiShell.Session -DistinguishedName $dn -Recursive:$recursive
    foreach ($m in $members) {
        Write-Host "$($m.samAccountName)  $($m.distinguishedName)"
    }
    Write-Host "Total: $($members.Count)"
}

function Invoke-AdsiShellMemberOf {
    param([string[]]$ShellArgs)
    Assert-AdsiShellConnected
    if ($ShellArgs.Count -lt 1) { throw 'Usage: memberof <#|dn> [-recursive]' }
    $recursive = $ShellArgs -contains '-recursive'
    $ref = ($ShellArgs | Where-Object { $_ -notmatch '^-(?i)recursive$' }) -join ' '
    $dn = Resolve-AdsiShellReferenceDn -Reference $ref
    $groups = Get-AdsiObjectMemberOf -Session $script:AdsiShell.Session -DistinguishedName $dn -Recursive:$recursive
    $groups | ForEach-Object { Write-Host $_ }
    Write-Host "Total: $($groups.Count)"
}

function Resolve-AdsiShellReferenceDn {
    param([string]$Reference)
    if ($Reference -match '^\d+$') {
        $hit = $script:AdsiShell.LastResults | Where-Object { $_.Index -eq [int]$Reference } | Select-Object -First 1
        if (-not $hit) { throw "No result #$Reference." }
        return [string]$hit.Object.distinguishedName
    }
    return $Reference
}

function Invoke-AdsiShellResolve {
    param([string[]]$ShellArgs)
    Assert-AdsiShellConnected
    if ($ShellArgs.Count -lt 1) { throw 'Usage: resolve <sid>' }
    $sid = $ShellArgs[0]
    Write-Host (Resolve-AdsiSidToName -Session $script:AdsiShell.Session -Sid $sid)
}

function Invoke-AdsiShellSchema {
    param([string[]]$ShellArgs)
    Assert-AdsiShellConnected
    if ($ShellArgs.Count -lt 1) { throw 'Usage: schema <className> (e.g. user, group, computer)' }
    $info = Get-AdsiSchemaClassAttributes -Session $script:AdsiShell.Session -ClassName $ShellArgs[0]
    Write-Host "Class: $($info.ClassName) (subClassOf: $($info.SubClassOf))"
    Write-Host ('Attributes: ' + ($info.Attributes -join ', '))
}

function Invoke-AdsiShellContext {
    param([string[]]$ShellArgs)
    Assert-AdsiShellConnected
    if ($ShellArgs.Count -lt 1) {
        Write-Host "Naming context: $(if ($script:AdsiShell.Session.NamingContext) { $script:AdsiShell.Session.NamingContext } else { 'Domain' })"
        return
    }
    $ctx = $ShellArgs[0]
    $map = @{ domain = 'Domain'; config = 'Config'; schema = 'Schema' }
    $key = $ctx.ToLowerInvariant()
    if (-not $map.Contains($key)) { throw 'Usage: context domain|config|schema' }
    $dn = Set-AdsiNamingContext -Session $script:AdsiShell.Session -Context $map[$key]
    Write-Host "Search base set to $dn ($($map[$key]))"
}

function Invoke-AdsiShellProfile {
    param([string[]]$ShellArgs)
    if ($ShellArgs.Count -eq 0) {
        Write-Host 'Usage: profile save <name> | profile load <name> | profile list'
        return
    }
    $verb = $ShellArgs[0].ToLowerInvariant()
    $profiles = Get-AdsiShellProfiles
    switch ($verb) {
        'list' {
            $profiles.Keys | Sort-Object | ForEach-Object { Write-Host $_ }
        }
        'save' {
            if ($ShellArgs.Count -lt 2) { throw 'Usage: profile save <name>' }
            Assert-AdsiShellConnected
            $name = $ShellArgs[1]
            $profiles[$name] = [pscustomobject]@{
                Server = $script:AdsiShell.Session.Server
                User   = $script:AdsiShell.Session.UserName
                BaseDn = $script:AdsiShell.Session.BaseDn
                GC     = $script:AdsiShell.Session.UseGlobalCatalog
            }
            Save-AdsiShellProfiles -Profiles $profiles
            Write-Host "Profile saved: $name"
        }
        'load' {
            if ($ShellArgs.Count -lt 2) { throw 'Usage: profile load <name>' }
            $name = $ShellArgs[1]
            if (-not $profiles.Contains($name)) { throw "Profile not found: $name" }
            $p = $profiles[$name]
            $user = [string]$p.User
            $secure = Read-Host "Password for $user" -AsSecureString
            $cred = if ($user) { New-Object pscredential($user, $secure) } else { $null }
            $script:AdsiShell.Session = Connect-AdsiDirectory -Server ([string]$p.Server) -BaseDn ([string]$p.BaseDn) -Credential $cred -UseGlobalCatalog:([bool]$p.GC)
            Write-Host "Loaded profile $name"
        }
        default { throw 'Usage: profile save|load|list' }
    }
}

function Invoke-AdsiShellAlias {
    param([string[]]$ShellArgs)
    if ($ShellArgs.Count -lt 2) {
        Write-Host 'Usage: alias <name> <command...> | alias list'
        return
    }
    if ($ShellArgs[0].Equals('list', [StringComparison]::OrdinalIgnoreCase)) {
        foreach ($entry in $script:AdsiShell.Aliases.GetEnumerator()) {
            Write-Host "$($entry.Key) => $($entry.Value)"
        }
        return
    }
    $name = $ShellArgs[0].ToLowerInvariant()
    $command = ($ShellArgs[1..($ShellArgs.Count - 1)] -join ' ')
    Test-AdsiShellAliasCommand -Command $command
    $script:AdsiShell.Aliases[$name] = $command
    Save-AdsiShellAliases -Aliases $script:AdsiShell.Aliases
    Write-Host "Alias set: $name => $command"
}

function Invoke-AdsiShellHistory {
    param([string[]]$ShellArgs)
    $count = 20
    if ($ShellArgs.Count -gt 0) { $count = [int]$ShellArgs[0] }
    $items = @($script:AdsiShell.History | Select-Object -Last $count)
    foreach ($item in $items) { Write-Host $item }
}

function Show-AdsiCommandHelp {
    param([string]$Topic)
    switch ($Topic.ToLowerInvariant()) {
        'list' {
            Write-Host 'list [type] [-filter "(...)"] [-query name] [-days N] [-props preset] [-sort attr]'
            Write-Host 'Queries: disabled, locked, pwd-never-expires, spn, empty-groups, stale-computers'
        }
        'search' {
            Write-Host 'search <attr> <value> [-type users|...] [-props preset] [-sort attr]'
            Write-Host 'Special attrs: anr, objectSid, objectGUID'
        }
        default { Show-AdsiShellHelp }
    }
}
