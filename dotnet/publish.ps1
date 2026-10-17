param(
    [switch]$SelfContained
)

$project = Join-Path $PSScriptRoot 'AdsiExplorer\AdsiExplorer.csproj'
$args = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64'
)

if ($SelfContained) {
    $args += @('--self-contained', 'true', '-p:PublishSingleFile=true')
}
else {
    $args += '--self-contained', 'false'
}

& dotnet @args
