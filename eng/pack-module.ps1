param(
    [Parameter(Mandatory = $true)]
    [string] $Project,
    [string] $OutputRoot = "modules/external",
    [string] $Publisher,
    [string] $PrivateKeyPath,
    [switch] $FolderOnly
)

$ErrorActionPreference = "Stop"
$projectPath = (Resolve-Path -LiteralPath $Project).Path
$projectDirectory = Split-Path -Parent $projectPath
$manifestPath = Join-Path $projectDirectory "module.json"
if (-not (Test-Path -LiteralPath $manifestPath)) { throw "The module project requires module.json." }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.id -notmatch '^[a-z0-9]+(?:[._-][a-z0-9]+)+$') { throw "module.json contains an invalid module ID." }

dotnet build $projectPath -c Release
if ($LASTEXITCODE -ne 0) { throw "Module build failed." }
$targetPath = (dotnet msbuild $projectPath -getProperty:TargetPath -p:Configuration=Release -nologo).Trim()
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $targetPath)) { throw "Could not resolve the module output assembly." }

$outputRootPath = [IO.Path]::GetFullPath($OutputRoot)
$packagePath = Join-Path $outputRootPath $manifest.id
if (Test-Path -LiteralPath $packagePath) { throw "Package destination already exists: $packagePath" }
$libraryPath = Join-Path $packagePath "lib"
$null = New-Item -ItemType Directory -Path $libraryPath
$destinationAssembly = Join-Path $libraryPath ([IO.Path]::GetFileName($targetPath))
Copy-Item -LiteralPath $targetPath -Destination $destinationAssembly
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $packagePath "module.json")
$hash = (Get-FileHash -LiteralPath $destinationAssembly -Algorithm SHA256).Hash
$checksumLine = "$hash  lib/$([IO.Path]::GetFileName($destinationAssembly))"
Set-Content -LiteralPath (Join-Path $packagePath "module.sha256") -Value $checksumLine -Encoding ascii
if ([string]::IsNullOrWhiteSpace($Publisher) -ne [string]::IsNullOrWhiteSpace($PrivateKeyPath)) {
    throw "Publisher and PrivateKeyPath must be provided together."
}
if (-not [string]::IsNullOrWhiteSpace($Publisher)) {
    if ($Publisher -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,99}$') { throw "Publisher contains invalid characters." }
    $resolvedKeyPath = (Resolve-Path -LiteralPath $PrivateKeyPath).Path
    $manifestBytes = [IO.File]::ReadAllBytes((Join-Path $packagePath "module.json"))
    $checksumBytes = [IO.File]::ReadAllBytes((Join-Path $packagePath "module.sha256"))
    $payload = [byte[]]::new($manifestBytes.Length + 1 + $checksumBytes.Length)
    [Array]::Copy($manifestBytes, 0, $payload, 0, $manifestBytes.Length)
    $payload[$manifestBytes.Length] = 10
    [Array]::Copy($checksumBytes, 0, $payload, $manifestBytes.Length + 1, $checksumBytes.Length)
    $signer = [Security.Cryptography.ECDsa]::Create()
    try {
        $signer.ImportFromPem([IO.File]::ReadAllText($resolvedKeyPath))
        $signature = $signer.SignData($payload, [Security.Cryptography.HashAlgorithmName]::SHA256)
    }
    finally { $signer.Dispose() }
    $signatureDocument = [ordered]@{ publisher = $Publisher; algorithm = "ECDSA-SHA256"; signature = [Convert]::ToBase64String($signature) }
    $signatureDocument | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packagePath "module.signature.json") -Encoding utf8NoBOM
}
if ($FolderOnly) {
    Write-Output $packagePath
    exit 0
}

$archivePath = Join-Path $outputRootPath "$($manifest.id)-$($manifest.version).erpmod"
if (Test-Path -LiteralPath $archivePath) { throw "Package archive already exists: $archivePath" }
$archiveItems = @((Join-Path $packagePath "module.json"), (Join-Path $packagePath "module.sha256"), (Join-Path $packagePath "lib"))
$signaturePath = Join-Path $packagePath "module.signature.json"
if (Test-Path -LiteralPath $signaturePath) { $archiveItems += $signaturePath }
$zipPath = [IO.Path]::ChangeExtension($archivePath, ".zip")
if (Test-Path -LiteralPath $zipPath) { throw "Temporary package archive already exists: $zipPath" }
Compress-Archive -LiteralPath $archiveItems -DestinationPath $zipPath
Move-Item -LiteralPath $zipPath -Destination $archivePath
Write-Output $archivePath
