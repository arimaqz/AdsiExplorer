#Requires -Version 5.1
Set-StrictMode -Version Latest

function ConvertTo-AdsiLdapDnList {
    param(
        [AllowNull()]
        $Value
    )

    if ($null -eq $Value) {
        return @()
    }

    if ($Value -is [string]) {
        if ([string]::IsNullOrWhiteSpace($Value)) {
            return @()
        }
        return ,@([string]$Value)
    }

    $list = New-Object System.Collections.Generic.List[string]
    foreach ($item in @($Value)) {
        if ($null -eq $item) { continue }
        $text = [string]$item
        if ([string]::IsNullOrWhiteSpace($text)) { continue }
        $list.Add($text) | Out-Null
    }
    return ,@($list.ToArray())
}

function Get-AdsiRootDseAttribute {
    param(
        [Parameter(Mandatory)]
        $Session,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $nc = $Session.RootEntry.Properties[$Name]
    if (-not $nc -or $nc.Count -eq 0) {
        return $null
    }

    return [string]$nc[0]
}

function Set-AdsiNamingContext {
    param(
        [Parameter(Mandatory)]
        $Session,

        [ValidateSet('Domain', 'Config', 'Schema')]
        [string]$Context = 'Domain'
    )

    $attr = switch ($Context) {
        'Domain' { 'defaultNamingContext' }
        'Config' { 'configurationNamingContext' }
        'Schema' { 'schemaNamingContext' }
    }

    $dn = Get-AdsiRootDseAttribute -Session $Session -Name $attr
    if (-not $dn) {
        throw "Could not read $attr from RootDSE."
    }

    Set-AdsiSearchBase -Session $Session -SearchBaseDn $dn | Out-Null
    $Session | Add-Member -NotePropertyName NamingContext -NotePropertyValue $Context -Force
    return $dn
}

function Get-AdsiNamedQueryFilter {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [int]$StaleDays = 90
    )

    $userBase = '(&(objectCategory=person)(objectClass=user)(!(objectClass=computer)))'
    switch ($Name.ToLowerInvariant()) {
        { $_ -in @('disabled', 'disabled-users', 'disabled_users') } {
            return "(&$userBase(userAccountControl:1.2.840.113556.1.4.803:=2))"
        }
        { $_ -in @('locked', 'locked-users', 'locked_users') } {
            return "(&$userBase(lockoutTime>=1))"
        }
        { $_ -in @('pwd-never-expires', 'password-never-expires', 'dont-expire') } {
            return "(&$userBase(userAccountControl:1.2.840.113556.1.4.803:=65536))"
        }
        { $_ -in @('spn', 'users-with-spn', 'users_with_spn') } {
            return "(&$userBase(servicePrincipalName=*))"
        }
        { $_ -in @('empty-groups', 'empty_groups', 'emptygroups') } {
            return '(&(objectClass=group)(!(member=*)))'
        }
        { $_ -in @('stale-computers', 'stale_computers', 'stalecomputers') } {
            if ($StaleDays -lt 1) {
                throw 'StaleDays must be >= 1.'
            }
            $threshold = [DateTime]::UtcNow.AddDays(-1 * $StaleDays).ToFileTimeUtc()
            return "(&(objectClass=computer)(lastLogonTimestamp<=$threshold))"
        }
        default {
            throw "Unknown named query '$Name'. Use: disabled, locked, pwd-never-expires, users-with-spn, empty-groups, stale-computers."
        }
    }
}

function New-AdsiIdentitySearchFilter {
    param(
        [Parameter(Mandatory)]
        [string]$Attribute,

        [Parameter(Mandatory)]
        [string]$Value,

        [string]$ObjectType = 'All'
    )

    $attr = $Attribute.ToLowerInvariant()
    switch ($attr) {
        'anr' {
            $escaped = if ($Value -match '[*]') {
                ($Value -split '\*' | ForEach-Object { Escape-LdapFilterValue -Value $_ }) -join '*'
            }
            else {
                Escape-LdapFilterValue -Value $Value
            }
            $typeFilter = if ($ObjectType -eq 'All') { '(objectClass=*)' } else { Get-AdsiPresetFilter -ObjectType $ObjectType }
            if ($ObjectType -eq 'All') {
                return "(anr=$escaped)"
            }
            return "(&$typeFilter(anr=$escaped))"
        }
        'objectsid' {
            return New-AdsiObjectSidFilter -Sid $Value.Trim()
        }
        'objectguid' {
            $guidText = $Value.Trim('{}', ' ')
            $guid = [guid]::Parse($guidText)
            $bytes = $guid.ToByteArray()
            $adBytes = [byte[]]@(
                $bytes[3], $bytes[2], $bytes[1], $bytes[0],
                $bytes[5], $bytes[4], $bytes[7], $bytes[6],
                $bytes[8], $bytes[9], $bytes[10], $bytes[11], $bytes[12], $bytes[13], $bytes[14], $bytes[15]
            )
            $hex = ($adBytes | ForEach-Object { '\' + $_.ToString('x2') }) -join ''
            return "(objectGUID=$hex)"
        }
        default {
            Test-LdapFilterAttributeName -Attribute $Attribute
            return New-AdsiSearchFilter -Attribute $Attribute -Value $Value -ObjectType $ObjectType
        }
    }
}

function Get-AdsiSchemaClassAttributes {
    param(
        [Parameter(Mandatory)]
        $Session,

        [Parameter(Mandatory)]
        [string]$ClassName
    )

    $savedBase = $Session.SearchBaseDn
    $schemaNc = Set-AdsiNamingContext -Session $Session -Context Schema

    $escapedClass = Escape-LdapFilterValue -Value $ClassName
    $classFilter = "(&(objectClass=classSchema)(|(lDAPDisplayName=$escapedClass)(cn=$escapedClass)))"
    $output = Search-AdsiObjects -Session $Session `
        -ObjectType All `
        -Filter $classFilter `
        -Properties @('lDAPDisplayName', 'mustContain', 'systemMustContain', 'mayContain', 'systemMayContain', 'subClassOf') `
        -SearchScope Subtree `
        -MaxResults 1 `
        -PageSize 500

    Set-AdsiSearchBase -Session $Session -SearchBaseDn $savedBase | Out-Null

    if ($output.Count -eq 0) {
        throw "Schema class not found: $ClassName"
    }

    $class = $output.Results[0]
    $attrs = New-Object System.Collections.Generic.HashSet[string]
    foreach ($prop in @('mustContain', 'systemMustContain', 'mayContain', 'systemMayContain')) {
        $val = $class.$prop
        if ($null -eq $val) { continue }
        foreach ($item in @($val)) {
            [void]$attrs.Add([string]$item)
        }
    }

    return [pscustomobject]@{
        ClassName  = $ClassName
        SubClassOf = $class.subClassOf
        Attributes = @($attrs | Sort-Object)
    }
}

function Resolve-AdsiSidToName {
    param(
        [Parameter(Mandatory)]
        $Session,

        [Parameter(Mandatory)]
        [string]$Sid
    )

    $filter = New-AdsiIdentitySearchFilter -Attribute objectSid -Value $Sid -ObjectType All
    $output = Search-AdsiObjects -Session $Session -ObjectType All -Filter $filter `
        -Properties @('distinguishedName', 'samAccountName', 'name') -MaxResults 1
    if ($output.Count -eq 0) {
        return $Sid
    }

    $obj = $output.Results[0]
    if ($obj.samAccountName) {
        return "$($obj.samAccountName) ($Sid)"
    }

    return "$($obj.distinguishedName) ($Sid)"
}

function Get-AdsiGroupMembers {
    param(
        [Parameter(Mandatory)]
        $Session,

        [Parameter(Mandatory)]
        [string]$DistinguishedName,

        [switch]$Recursive,

        [int]$MaxDepth = 8
    )

    $filter = "(distinguishedName=$(Escape-LdapFilterValue -Value $DistinguishedName))"
    $group = Search-AdsiObjects -Session $Session -ObjectType Groups -Filter $filter `
        -Properties @('distinguishedName', 'member', 'name', 'samAccountName') -MaxResults 1
    if ($group.Count -eq 0) {
        throw "Group not found: $DistinguishedName"
    }

    $members = ConvertTo-AdsiLdapDnList -Value $group.Results[0].member
    if ($members.Count -eq 0) {
        return @()
    }

    $results = New-Object System.Collections.Generic.List[object]
    $seen = New-Object System.Collections.Generic.HashSet[string]

    function Add-MemberObjects {
        param([string[]]$MemberDns, [int]$Depth)

        foreach ($dn in $MemberDns) {
            if ([string]::IsNullOrWhiteSpace($dn)) { continue }
            if ($seen.Contains($dn)) { continue }
            [void]$seen.Add($dn)

            $obj = Search-AdsiObjects -Session $Session -ObjectType All `
                -Filter "(distinguishedName=$(Escape-LdapFilterValue -Value $dn))" `
                -Properties @('distinguishedName', 'samAccountName', 'name', 'objectClass', 'member') `
                -MaxResults 1
            if ($obj.Count -eq 0) {
                $results.Add([pscustomobject]@{ distinguishedName = $dn; samAccountName = $null; name = $null; objectClass = $null }) | Out-Null
                continue
            }

            $item = $obj.Results[0]
            $results.Add($item) | Out-Null

            if ($Recursive -and $Depth -lt $MaxDepth) {
                $nested = ConvertTo-AdsiLdapDnList -Value $item.member
                if ($nested.Count -gt 0) {
                    Add-MemberObjects -MemberDns $nested -Depth ($Depth + 1)
                }
            }
        }
    }

    Add-MemberObjects -MemberDns $members -Depth 1
    return ,@($results.ToArray())
}

function Get-AdsiObjectMemberOf {
    param(
        [Parameter(Mandatory)]
        $Session,

        [Parameter(Mandatory)]
        [string]$DistinguishedName,

        [switch]$Recursive
    )

    $filter = "(distinguishedName=$(Escape-LdapFilterValue -Value $DistinguishedName))"
    $obj = Search-AdsiObjects -Session $Session -ObjectType All -Filter $filter `
        -Properties @('memberOf') -MaxResults 1
    if ($obj.Count -eq 0) {
        throw "Object not found: $DistinguishedName"
    }

    $groups = ConvertTo-AdsiLdapDnList -Value $obj.Results[0].memberOf
    if (-not $Recursive) {
        return $groups
    }

    $all = New-Object System.Collections.Generic.HashSet[string]
    $queue = New-Object System.Collections.Generic.Queue[string]
    foreach ($g in $groups) { $queue.Enqueue($g) | Out-Null }

    while ($queue.Count -gt 0) {
        $dn = $queue.Dequeue()
        if ([string]::IsNullOrWhiteSpace($dn)) { continue }
        if ($all.Contains($dn)) { continue }
        [void]$all.Add($dn)

        $parent = Search-AdsiObjects -Session $Session -ObjectType Groups `
            -Filter "(distinguishedName=$(Escape-LdapFilterValue -Value $dn))" `
            -Properties @('memberOf') -MaxResults 1
        if ($parent.Count -eq 0) { continue }
        foreach ($p in (ConvertTo-AdsiLdapDnList -Value $parent.Results[0].memberOf)) {
            $queue.Enqueue($p) | Out-Null
        }
    }

    $output = New-Object System.Collections.Generic.List[string]
    foreach ($entry in $all) {
        [void]$output.Add([string]$entry)
    }
    return [string[]]$output.ToArray()
}

function Get-AdsiOuTree {
    param(
        [Parameter(Mandatory)]
        $Session,

        [string]$DepthPrefix = ''
    )

    $output = Search-AdsiObjects -Session $Session -ObjectType OrganizationalUnits `
        -SearchScope OneLevel `
        -Properties @('distinguishedName', 'name', 'description') `
        -MaxResults 0 `
        -PageSize 500

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($ou in ($output.Results | Sort-Object name)) {
        $line = if ($DepthPrefix) { "$DepthPrefix$($ou.name) [$($ou.distinguishedName)]" } else { "$($ou.name) [$($ou.distinguishedName)]" }
        $lines.Add($line) | Out-Null

        $savedBase = $Session.SearchBaseDn
        Set-AdsiSearchBase -Session $Session -SearchBaseDn $ou.distinguishedName | Out-Null
        $childLines = Get-AdsiOuTree -Session $Session -DepthPrefix ($DepthPrefix + '  ')
        foreach ($child in $childLines) {
            $lines.Add($child) | Out-Null
        }
        Set-AdsiSearchBase -Session $Session -SearchBaseDn $savedBase | Out-Null
    }

    return [string[]]$lines.ToArray()
}
