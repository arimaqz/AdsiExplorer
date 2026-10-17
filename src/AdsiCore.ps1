#Requires -Version 5.1
Set-StrictMode -Version Latest

function Escape-LdapFilterValue {
    param(
        [AllowNull()]
        [string]$Value
    )

    if ($null -eq $Value) {
        return ''
    }

    $builder = New-Object System.Text.StringBuilder
    foreach ($char in $Value.ToCharArray()) {
        switch ($char) {
            '\' { [void]$builder.Append('\5c') }
            '*' { [void]$builder.Append('\2a') }
            '(' { [void]$builder.Append('\28') }
            ')' { [void]$builder.Append('\29') }
            "`0" { [void]$builder.Append('\00') }
            default { [void]$builder.Append($char) }
        }
    }

    return $builder.ToString()
}

function Get-AdsiPresetFilter {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Users', 'Groups', 'Computers', 'OrganizationalUnits', 'All')]
        [string]$ObjectType
    )

    switch ($ObjectType) {
        'Users' {
            return '(&(objectCategory=person)(objectClass=user)(!(objectClass=computer)))'
        }
        'Groups' {
            return '(objectClass=group)'
        }
        'Computers' {
            return '(objectClass=computer)'
        }
        'OrganizationalUnits' {
            return '(objectClass=organizationalUnit)'
        }
        'All' {
            return '(objectClass=*)'
        }
    }
}

function New-AdsiSearchFilter {
    param(
        [Parameter(Mandatory)]
        [string]$Attribute,

        [Parameter(Mandatory)]
        [string]$Value,

        [string]$ObjectType = 'Users'
    )

    if ([string]::IsNullOrWhiteSpace($Attribute)) {
        throw 'Attribute cannot be empty.'
    }

    $escapedValue = if ($Value -match '[*]') {
        ($Value -split '\*' | ForEach-Object { Escape-LdapFilterValue -Value $_ }) -join '*'
    }
    else {
        Escape-LdapFilterValue -Value $Value
    }

    $typeFilter = Get-AdsiPresetFilter -ObjectType $ObjectType
    if ($ObjectType -eq 'All') {
        return "($Attribute=$escapedValue)"
    }

    return "(&$typeFilter($Attribute=$escapedValue))"
}

function Convert-AdsiPathToDn {
    param(
        [AllowNull()]
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $Path
    }

    if ($Path -match '^LDAP://[^/]+/(.+)$') {
        return $Matches[1]
    }

    return $Path
}

function ConvertFrom-AdsiUserAccountControl {
    param(
        [AllowNull()]
        $Value
    )

    if ($null -eq $Value) {
        return @()
    }

    $uac = [int]$Value
    $map = @(
        0x0002, 'ACCOUNTDISABLE'
        0x0010, 'LOCKOUT'
        0x0020, 'PASSWD_NOTREQD'
        0x10000, 'DONT_EXPIRE_PASSWORD'
        0x800000, 'PASSWORD_EXPIRED'
    )

    $active = @()
    for ($i = 0; $i -lt $map.Count; $i += 2) {
        if (($uac -band $map[$i]) -eq $map[$i]) {
            $active += $map[$i + 1]
        }
    }

    return $active
}

function Test-AdsiAccountDisabled {
    param($UserAccountControl)

    if ($null -eq $UserAccountControl) {
        return $false
    }

    return (([int]$UserAccountControl -band 2) -eq 2)
}

function Format-AdsiPropertyValue {
    param(
        [AllowNull()]
        $Value,

        [string]$AttributeName
    )

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [System.DirectoryServices.ResultPropertyValueCollection] -or
        $Value -is [System.Collections.IEnumerable] -and $Value -isnot [string] -and $Value -isnot [byte[]]) {
        $items = @($Value | ForEach-Object { Format-AdsiPropertyValue -Value $_ -AttributeName $AttributeName })
        if ($items.Count -le 1) {
            return ($items -join '')
        }
        return ($items -join '; ')
    }

    if ($Value -is [byte[]]) {
        $guidAttributes = @(
            'objectGUID', 'objectSid', 'sidHistory', 'ms-DS-ConsistencyGuid',
            'msExchMailboxGuid', 'ms-DS-SourceObjectDN'
        )
        if ($guidAttributes -contains $AttributeName -and $Value.Length -eq 16) {
            return ([guid]$Value).ToString()
        }
        if ($AttributeName -eq 'objectSid') {
            try {
                return (New-Object System.Security.Principal.SecurityIdentifier($Value, 0)).Value
            }
            catch {
                return [Convert]::ToBase64String($Value)
            }
        }
        return [Convert]::ToBase64String($Value)
    }

    if ($AttributeName -ieq 'userAccountControl' -and ($Value -is [int] -or $Value -is [long] -or $Value -match '^\d+$')) {
        $decoded = ConvertFrom-AdsiUserAccountControl -Value $Value
        if ($decoded.Count -gt 0) {
            return "$Value ($($decoded -join ', '))"
        }
        return [string]$Value
    }

    if ($Value -is [long] -or $Value -is [int]) {
        $fileTimeAttributes = @(
            'lastLogon', 'lastLogonTimestamp', 'pwdLastSet', 'accountExpires',
            'badPasswordTime', 'lockoutTime', 'lastLogoff', 'ms-Mcs-AdmPwdExpirationTime'
        )
        if ($fileTimeAttributes -contains $AttributeName) {
            if ($Value -eq 0 -or $Value -eq 9223372036854775807) {
                return $null
            }
            try {
                return [DateTime]::FromFileTime($Value).ToString('o')
            }
            catch {
                return $Value
            }
        }
    }

    return [string]$Value
}

function Connect-AdsiDirectory {
    param(
        [string]$Server,

        [string]$BaseDn,

        [pscredential]$Credential,

        [switch]$UseGlobalCatalog
    )

    $username = $null
    $password = $null
    if ($Credential) {
        $username = $Credential.UserName
        $secure = $Credential.Password
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try {
            $password = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
        }
        finally {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        }
    }

    $protocol = if ($UseGlobalCatalog) { 'GC' } else { 'LDAP' }
    $rootPath = if ($Server) { "$protocol`://$Server/RootDSE" } else { "$protocol`://RootDSE" }

    $rootEntry = if ($username) {
        New-Object System.DirectoryServices.DirectoryEntry($rootPath, $username, $password)
    }
    else {
        New-Object System.DirectoryServices.DirectoryEntry($rootPath)
    }

    $resolvedBase = $BaseDn
    if (-not $resolvedBase) {
        $nc = $rootEntry.Properties['defaultNamingContext']
        if (-not $nc -or $nc.Count -eq 0) {
            throw "Could not read defaultNamingContext from $rootPath"
        }
        $resolvedBase = [string]$nc[0]
    }

    $searchBasePath = if ($Server) {
        "$protocol`://$Server/$resolvedBase"
    }
    else {
        "$protocol`://$resolvedBase"
    }

    $searchEntry = if ($username) {
        New-Object System.DirectoryServices.DirectoryEntry($searchBasePath, $username, $password)
    }
    else {
        New-Object System.DirectoryServices.DirectoryEntry($searchBasePath)
    }

    $null = $searchEntry.RefreshCache()

    [pscustomobject]@{
        Server           = $Server
        BaseDn           = $resolvedBase
        SearchBaseDn     = $resolvedBase
        UseGlobalCatalog = [bool]$UseGlobalCatalog
        RootEntry        = $rootEntry
        SearchEntry      = $searchEntry
        Credential       = $Credential
        UserName         = $username
        Password         = $password
    }
}

function Get-AdsiDirectoryEntry {
    param(
        [Parameter(Mandatory)]
        $Session,

        [Parameter(Mandatory)]
        [string]$Path
    )

    if ($Session.UserName) {
        return New-Object System.DirectoryServices.DirectoryEntry($Path, $Session.UserName, $Session.Password)
    }

    return New-Object System.DirectoryServices.DirectoryEntry($Path)
}

function Set-AdsiSearchBase {
    param(
        [Parameter(Mandatory)]
        $Session,

        [Parameter(Mandatory)]
        [string]$SearchBaseDn
    )

    $protocol = if ($Session.UseGlobalCatalog) { 'GC' } else { 'LDAP' }
    $searchBasePath = if ($Session.Server) {
        "$protocol`://$($Session.Server)/$SearchBaseDn"
    }
    else {
        "$protocol`://$SearchBaseDn"
    }

    $Session.SearchEntry = Get-AdsiDirectoryEntry -Session $Session -Path $searchBasePath
    $null = $Session.SearchEntry.RefreshCache()
    $Session.SearchBaseDn = $SearchBaseDn
    return $Session
}

function Get-AdsiDirectoryPropertyValues {
    param(
        [AllowNull()]
        $Raw
    )

    if ($null -eq $Raw) {
        return @()
    }

    if ($Raw -is [System.DirectoryServices.PropertyValueCollection]) {
        $list = New-Object System.Collections.Generic.List[object]
        foreach ($item in $Raw) {
            $list.Add($item) | Out-Null
        }
        return $list.ToArray()
    }

    return @(Get-AdsiRawPropertyValues -Raw $Raw)
}

function Get-AdsiAllObjectProperties {
    param(
        [Parameter(Mandatory)]
        $Session,

        [Parameter(Mandatory)]
        [string]$DistinguishedName
    )

    $protocol = if ($Session.UseGlobalCatalog) { 'GC' } else { 'LDAP' }
    $objectPath = if ($Session.Server) {
        "$protocol`://$($Session.Server)/$DistinguishedName"
    }
    else {
        "$protocol`://$DistinguishedName"
    }

    $entry = Get-AdsiDirectoryEntry -Session $Session -Path $objectPath
    $null = $entry.RefreshCache()

    $props = [ordered]@{}
    foreach ($name in $entry.Properties.PropertyNames) {
        $attributeName = [string]$name
        $values = @(Get-AdsiDirectoryPropertyValues -Raw $entry.Properties[$name])
        if ($values.Length -gt 1) {
            $props[$attributeName] = @($values | ForEach-Object {
                Format-AdsiPropertyValue -Value $_ -AttributeName $attributeName
            })
        }
        elseif ($values.Length -eq 1) {
            $props[$attributeName] = Format-AdsiPropertyValue -Value $values[0] -AttributeName $attributeName
        }
        else {
            $props[$attributeName] = $null
        }
    }

    if (-not $props.Contains('distinguishedName')) {
        $props['distinguishedName'] = $DistinguishedName
    }

    return [pscustomobject]$props
}

function Get-AdsiSearchResultProperty {
    param(
        [Parameter(Mandatory)]
        [System.DirectoryServices.SearchResult]$SearchResult,

        [Parameter(Mandatory)]
        [string]$PropertyName
    )

    foreach ($candidate in $SearchResult.Properties.PropertyNames) {
        if ([string]$candidate -ieq $PropertyName) {
            return $SearchResult.Properties[$candidate]
        }
    }

    return $null
}

function Get-AdsiRawPropertyValues {
    param(
        [AllowNull()]
        $Raw
    )

    if ($null -eq $Raw) {
        return @()
    }

    if ($Raw -is [System.DirectoryServices.ResultPropertyValueCollection]) {
        $list = New-Object System.Collections.Generic.List[object]
        foreach ($item in $Raw) {
            $list.Add($item) | Out-Null
        }
        return $list.ToArray()
    }

    if ($Raw -is [System.Collections.IEnumerable] -and $Raw -isnot [string] -and $Raw -isnot [byte[]]) {
        return @($Raw | ForEach-Object { $_ })
    }

    return @($Raw)
}

function Get-AdsiObjectProperties {
    param(
        [Parameter(Mandatory)]
        [System.DirectoryServices.SearchResult]$SearchResult,

        [string[]]$PropertyNames
    )

    $props = @{}
    $names = if (@($PropertyNames).Count -gt 0) {
        @($PropertyNames | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    }
    else {
        @($SearchResult.Properties.PropertyNames | ForEach-Object { [string]$_ })
    }

    foreach ($name in $names) {
        $raw = Get-AdsiSearchResultProperty -SearchResult $SearchResult -PropertyName $name
        $values = @(Get-AdsiRawPropertyValues -Raw $raw)
        if ($values.Length -gt 1) {
            $props[$name] = @($values | ForEach-Object { Format-AdsiPropertyValue -Value $_ -AttributeName $name })
        }
        elseif ($values.Length -eq 1) {
            $props[$name] = Format-AdsiPropertyValue -Value $values[0] -AttributeName $name
        }
        else {
            $props[$name] = $null
        }
    }

    if ($SearchResult.Properties.Contains('distinguishedname')) {
        $props['distinguishedName'] = [string]$SearchResult.Properties['distinguishedname'][0]
    }
    elseif ($SearchResult.Properties.Contains('adspath')) {
        $props['distinguishedName'] = Convert-AdsiPathToDn -Path ([string]$SearchResult.Properties['adspath'][0])
    }

    return [pscustomobject]$props
}

function Search-AdsiObjects {
    param(
        [Parameter(Mandatory)]
        $Session,

        [ValidateSet('Users', 'Groups', 'Computers', 'OrganizationalUnits', 'All')]
        [string]$ObjectType = 'Users',

        [string]$Filter,

        [string]$Attribute,

        [string]$Value,

        [string[]]$Properties = @('distinguishedName', 'samAccountName', 'name', 'objectClass'),

        [ValidateSet('Base', 'OneLevel', 'Subtree')]
        [string]$SearchScope = 'Subtree',

        [int]$PageSize = 1000,

        [int]$SizeLimit = 0,

        [int]$MaxResults = 100,

        [int]$Skip = 0,

        [switch]$CountOnly,

        [string]$SortProperty
    )

    if (-not $Session.SearchEntry) {
        throw 'Session is missing SearchEntry. Use Connect-AdsiDirectory first.'
    }

    $ldapFilter = $Filter
    if (-not $ldapFilter) {
        if ($Attribute -and $null -ne $Value) {
            $ldapFilter = New-AdsiSearchFilter -Attribute $Attribute -Value $Value -ObjectType $ObjectType
        }
        else {
            $ldapFilter = Get-AdsiPresetFilter -ObjectType $ObjectType
        }
    }

    $searcher = New-Object System.DirectoryServices.DirectorySearcher($Session.SearchEntry)
    $searcher.Filter = $ldapFilter
    $searcher.SearchScope = [enum]::Parse([System.DirectoryServices.SearchScope], $SearchScope)
    $searcher.PageSize = [Math]::Max(1, $PageSize)
    if ($SizeLimit -gt 0) {
        $searcher.SizeLimit = $SizeLimit
    }

    $searcher.PropertiesToLoad.Clear() | Out-Null
    $propertyList = @($Properties | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)
    if (@($propertyList).Count -gt 0) {
        foreach ($prop in $propertyList) {
            $loadName = if ($prop -ieq 'distinguishedName') { 'distinguishedname' } else { $prop.ToLowerInvariant() }
            [void]$searcher.PropertiesToLoad.Add($loadName)
        }
        if (-not $searcher.PropertiesToLoad.Contains('distinguishedname')) {
            [void]$searcher.PropertiesToLoad.Add('distinguishedname')
        }
    }

    $results = @()
    $skipped = 0
    $matched = 0
    $hasMore = $false
    $found = $searcher.FindAll()
    try {
        if ($CountOnly) {
            foreach ($entry in $found) {
                $matched++
            }

            return [pscustomobject]@{
                Filter    = $ldapFilter
                Count     = $matched
                Truncated = $false
                HasMore   = $false
                Skip      = $Skip
                Results   = @()
            }
        }

        foreach ($entry in $found) {
            if ($skipped -lt $Skip) {
                $skipped++
                continue
            }

            $results += Get-AdsiObjectProperties -SearchResult $entry -PropertyNames $Properties
            if ($MaxResults -gt 0 -and $results.Count -ge $MaxResults) {
                $hasMore = $true
                break
            }
        }

        if (-not $hasMore) {
            $enumerated = 0
            foreach ($entry in $found) {
                $enumerated++
            }
            $hasMore = ($Skip + $results.Count) -lt $enumerated
        }
    }
    finally {
        $found.Dispose()
    }

    if ($SortProperty -and $results.Count -gt 1) {
        $results = @($results | Sort-Object { $_.$SortProperty })
    }

    [pscustomobject]@{
        Filter       = $ldapFilter
        Count        = $results.Count
        Truncated    = ($MaxResults -gt 0 -and $results.Count -ge $MaxResults)
        HasMore      = $hasMore
        Skip         = $Skip
        Results      = $results
    }
}
