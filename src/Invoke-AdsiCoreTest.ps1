#Requires -Version 5.1
param(
    [Parameter(Mandatory)]
    [string]$Server,

    [string]$Username,

    [securestring]$Password,

    [int]$MaxResults = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptDir 'AdsiCore.ps1')

$credential = $null
if ($Username -and $Password) {
    $credential = New-Object pscredential($Username, $Password)
}

Write-Host "Connecting to $Server ..."
$session = Connect-AdsiDirectory -Server $Server -Credential $credential
Write-Host "Base DN: $($session.BaseDn)"
Write-Host ''

Write-Host "Listing users (first $MaxResults) ..."
$userList = Search-AdsiObjects -Session $session -ObjectType Users -MaxResults $MaxResults
Write-Host "Filter: $($userList.Filter)"
$userList.Results | Format-Table distinguishedName, samAccountName, name -AutoSize

$sampleSam = $userList.Results | Where-Object { $_.samAccountName } | Select-Object -First 1 -ExpandProperty samAccountName
if ($sampleSam) {
    Write-Host "Searching samAccountName = $sampleSam ..."
    $one = Search-AdsiObjects -Session $session -ObjectType Users -Attribute 'samAccountName' -Value $sampleSam `
        -Properties @('distinguishedName', 'samAccountName', 'userPrincipalName', 'mail', 'memberOf') -MaxResults 3
    $one.Results | Format-List
}

Write-Host "Done."
