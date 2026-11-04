param(
    [switch] $SkipPostRestoreBrowserWorkflow
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$composeFile = Join-Path $repositoryRoot "docker-compose.yml"
$workRoot = Join-Path ([IO.Path]::GetTempPath()) "erp-recovery-$([Guid]::NewGuid().ToString('N'))"
$projectSuffix = [Guid]::NewGuid().ToString("N").Substring(0, 12)
$projects = @("erp-recovery-source-$projectSuffix", "erp-recovery-target-a-$projectSuffix", "erp-recovery-target-b-$projectSuffix")
$environmentKeys = @(
    "POSTGRES_DB", "POSTGRES_USER", "POSTGRES_PASSWORD", "POSTGRES_PORT", "ERP_PORT",
    "ERP_ADMIN_EMAIL", "ERP_ADMIN_PASSWORD", "MODULE_SIGNATURES_REQUIRED",
    "ERP_E2E_BASE_URL", "ERP_E2E_EMAIL", "ERP_E2E_PASSWORD"
)
$previousEnvironment = @{}
$timings = [ordered]@{}
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$adminEmail = "recovery-$projectSuffix@example.test"
$adminPassword = "Recovery!Aa7$projectSuffix"
$postgresPassword = [Guid]::NewGuid().ToString("N")
$packageDirectory = Join-Path $workRoot "packages"
$backupDirectory = Join-Path $workRoot "backup"
$sourceEnvFile = Join-Path $workRoot "source.env"
$targetAEnvFile = Join-Path $workRoot "target-a.env"
$targetBEnvFile = Join-Path $workRoot "target-b.env"
$attachmentContent = [Text.Encoding]::UTF8.GetBytes("Synthetic recovery attachment $projectSuffix")
$attachmentFile = Join-Path $workRoot "recovery.txt"
$downloadFile = Join-Path $workRoot "downloaded.txt"
$attachmentId = $null
$sourceContext = $null
$replayEventId = $null
$replayOrderId = $null
$replayAuditCount = 0
$startTime = [DateTimeOffset]::UtcNow

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Write-ComposeEnvironment([string] $path, [int] $applicationPort, [int] $databasePort) {
    @(
        "POSTGRES_DB=erp",
        "POSTGRES_USER=erp",
        "POSTGRES_PASSWORD=$postgresPassword",
        "POSTGRES_PORT=127.0.0.1:$databasePort",
        "ERP_PORT=127.0.0.1:$applicationPort",
        "ERP_ADMIN_EMAIL=$adminEmail",
        "ERP_ADMIN_PASSWORD=$adminPassword",
        "MODULE_SIGNATURES_REQUIRED=false"
    ) | Set-Content -LiteralPath $path -Encoding ascii
}

function Invoke-Compose([string] $project, [string] $environmentFile, [string[]] $arguments) {
    $saved = @{}
    foreach ($key in $environmentKeys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key, "Process")
    }
    try {
        foreach ($line in (Get-Content -LiteralPath $environmentFile)) {
            $separator = $line.IndexOf("=")
            if ($separator -gt 0) {
                $name = $line.Substring(0, $separator)
                [Environment]::SetEnvironmentVariable($name, $line.Substring($separator + 1), "Process")
            }
        }
        & docker compose --project-name $project --env-file $environmentFile --file $composeFile @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "docker compose failed for the isolated project '$project' (exit code $LASTEXITCODE)."
        }
    }
    finally {
        foreach ($key in $environmentKeys) {
            [Environment]::SetEnvironmentVariable($key, $saved[$key], "Process")
        }
    }
}

function Invoke-Docker([string[]] $arguments) {
    & docker @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker failed (exit code $LASTEXITCODE)."
    }
}

function Get-ContainerId([string] $project, [string] $environmentFile, [string] $service) {
    $containerId = & docker compose --project-name $project --env-file $environmentFile --file $composeFile ps --all --quiet $service
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($containerId)) {
        throw "Could not resolve the '$service' container in isolated project '$project'."
    }
    return $containerId.Trim()
}

function Assert-DisposableProject([string] $project) {
    $existing = & docker volume ls --quiet --filter "label=com.docker.compose.project=$project"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not verify Docker volume ownership for '$project'."
    }
    if (-not [string]::IsNullOrWhiteSpace(($existing -join ""))) {
        throw "Refusing to use project '$project': Docker volumes with that project label already exist."
    }

    $existing = & docker ps --all --quiet --filter "label=com.docker.compose.project=$project"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not verify container ownership for '$project'."
    }
    if (-not [string]::IsNullOrWhiteSpace(($existing -join ""))) {
        throw "Refusing to use project '$project': containers with that project label already exist."
    }

    $existing = & docker network ls --quiet --filter "label=com.docker.compose.project=$project"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not verify network ownership for '$project'."
    }
    if (-not [string]::IsNullOrWhiteSpace(($existing -join ""))) {
        throw "Refusing to use project '$project': networks with that project label already exist."
    }
}

function Wait-ForApplication([string] $baseUrl) {
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(3)
    do {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/health/ready" -TimeoutSec 5
            if ($response.StatusCode -eq 200) { return }
        }
        catch {
            Start-Sleep -Seconds 3
        }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Application readiness did not succeed at $baseUrl/health/ready."
}

function Invoke-Login([string] $baseUrl) {
    $loginPage = Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/account/login" -WebSession $session -TimeoutSec 15
    $tokenMatch = [regex]::Match(
        $loginPage.Content,
        'name="__RequestVerificationToken"[^>]*value="([^"]+)"',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $tokenMatch.Success) {
        $tokenMatch = [regex]::Match(
            $loginPage.Content,
            'value="([^"]+)"[^>]*name="__RequestVerificationToken"',
            [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }
    if (-not $tokenMatch.Success) { throw "The login page did not provide an antiforgery token." }

    $form = @{
        email = $adminEmail
        password = $adminPassword
        __RequestVerificationToken = [Net.WebUtility]::HtmlDecode($tokenMatch.Groups[1].Value)
    }
    $null = Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/account/login" -Method Post -Body $form -WebSession $session -TimeoutSec 30
    $context = Invoke-RestMethod -Uri "$baseUrl/api/v1/platform/context" -WebSession $session -TimeoutSec 15
    return $context
}

function Invoke-Sql([string] $project, [string] $environmentFile, [string] $sql) {
    $containerId = Get-ContainerId $project $environmentFile "postgres"
    $queryFile = Join-Path $workRoot "recovery-query.sql"
    [IO.File]::WriteAllText($queryFile, $sql, [Text.UTF8Encoding]::new($false))
    Invoke-Docker @("cp", $queryFile, "${containerId}:/tmp/recovery-query.sql")
    $result = & docker exec $containerId psql --username=erp --dbname=erp --quiet --tuples-only --no-align --file=/tmp/recovery-query.sql
    if ($LASTEXITCODE -ne 0) { throw "A verification query failed in the disposable PostgreSQL container." }
    return ($result -join "`n").Trim()
}

function Wait-ForReplay([string] $project, [string] $environmentFile, [string] $eventId) {
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(2)
    do {
        $state = Invoke-Sql $project $environmentFile @"
SELECT CASE WHEN "ProcessedAtUtc" IS NOT NULL AND "DeadLetteredAtUtc" IS NULL
    THEN 'processed' ELSE 'pending' END
FROM platform.outbox_messages WHERE "Id" = '$eventId'::uuid;
"@
        if ($state -eq "processed") { return }
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "The restored fulfillment event was not processed before the verification timeout."
}

function Assert-RecoveredAssets(
    [string] $baseUrl,
    [string] $expectedTenantId,
    [string] $expectedCompanyId,
    [string] $expectedAttachmentEntityId) {
    $context = Invoke-RestMethod -Uri "$baseUrl/api/v1/platform/context" -WebSession $session -TimeoutSec 15
    if ($context.tenantId.value -ne $expectedTenantId -or $context.companyId.value -ne $expectedCompanyId) {
        throw "The restored authenticated session resolved to a different tenant or company."
    }

    $attachments = Invoke-RestMethod `
        -Uri "$baseUrl/api/v1/platform/attachments?entityType=RecoveryTest&entityId=$expectedAttachmentEntityId" `
        -WebSession $session -TimeoutSec 15
    $matchingAttachment = @($attachments | Where-Object {
        $_.id -eq $attachmentId -and $_.entityType -eq "RecoveryTest" -and $_.entityId -eq $expectedAttachmentEntityId
    })
    if ($matchingAttachment.Count -ne 1) {
        throw "The restored attachment metadata is missing its synthetic record association."
    }

    $modules = Invoke-RestMethod -Uri "$baseUrl/api/v1/platform/modules" -WebSession $session -TimeoutSec 15
    $externalModule = @($modules | Where-Object { $_.id -eq "community.hello" -and $_.version -eq "0.1.0" })
    if ($externalModule.Count -ne 1 -or -not $externalModule[0].enabled) {
        throw "The restored external module package or enabled state is missing."
    }

    $hello = Invoke-RestMethod -Uri "$baseUrl/api/v1/community/hello" -WebSession $session -TimeoutSec 15
    if ($hello.message -ne "Community Hello was loaded from a verified .erpmod package.") {
        throw "The restored external module endpoint did not return its expected response."
    }

    $null = Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/api/v1/platform/attachments/$attachmentId/content" `
        -WebSession $session -OutFile $downloadFile -TimeoutSec 15
    $expectedHash = (Get-FileHash -LiteralPath $attachmentFile -Algorithm SHA256).Hash
    $actualHash = (Get-FileHash -LiteralPath $downloadFile -Algorithm SHA256).Hash
    if ($expectedHash -ne $actualHash) { throw "The restored attachment content hash does not match the uploaded asset." }

    $missingProduct = [Guid]::NewGuid()
    try {
        $null = Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/api/v1/products/$missingProduct" `
            -WebSession $session -TimeoutSec 15
        throw "A non-existent company-scoped product unexpectedly returned success."
    }
    catch {
        $statusCode = [int]$_.Exception.Response.StatusCode
        if ($statusCode -ne 404) {
            throw "A non-existent company-scoped product returned HTTP $statusCode rather than 404."
        }
    }

    return $context
}

try {
    foreach ($key in $environmentKeys) {
        $previousEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, "Process")
    }

    New-Item -ItemType Directory -Path $workRoot, $packageDirectory, $backupDirectory | Out-Null
    [IO.File]::WriteAllBytes($attachmentFile, $attachmentContent)

    $sourcePort = Get-FreeTcpPort
    $sourceDatabasePort = Get-FreeTcpPort
    $targetAPort = Get-FreeTcpPort
    $targetADatabasePort = Get-FreeTcpPort
    $targetBPort = Get-FreeTcpPort
    $targetBDatabasePort = Get-FreeTcpPort
    Write-ComposeEnvironment $sourceEnvFile $sourcePort $sourceDatabasePort
    Write-ComposeEnvironment $targetAEnvFile $targetAPort $targetADatabasePort
    Write-ComposeEnvironment $targetBEnvFile $targetBPort $targetBDatabasePort
    foreach ($project in $projects) { Assert-DisposableProject $project }
    $sourceUrl = "http://127.0.0.1:$sourcePort"
    $sourceTimer = [Diagnostics.Stopwatch]::StartNew()
    Write-Output "Starting isolated synthetic source installation ($($projects[0]))."
    Invoke-Compose $projects[0] $sourceEnvFile @("up", "--detach", "--build", "--wait")
    Wait-ForApplication $sourceUrl
    $sourceContext = Invoke-Login $sourceUrl

    $packagePath = Join-Path $packageDirectory "community.hello-0.1.0.erpmod"
    & (Join-Path $repositoryRoot "eng\pack-module.ps1") `
        -Project (Join-Path $repositoryRoot "samples\CommunityHello\CommunityHello.csproj") `
        -OutputRoot $packageDirectory
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $packagePath)) {
        throw "Could not build the synthetic external module package."
    }

    $uploadResponse = Invoke-WebRequest -UseBasicParsing -Uri "$sourceUrl/api/v1/platform/attachments?entityType=RecoveryTest&entityId=$projectSuffix" `
        -Method Post -InFile $attachmentFile -ContentType "text/plain" `
        -Headers @{ "X-File-Name" = "recovery.txt" } -WebSession $session -TimeoutSec 30
    if ($uploadResponse.StatusCode -ne 201) { throw "Synthetic attachment upload did not return HTTP 201." }
    $attachment = $uploadResponse.Content | ConvertFrom-Json
    $attachmentId = $attachment.id

    $null = Invoke-WebRequest -UseBasicParsing -Uri "$sourceUrl/api/v1/platform/modules/install" -Method Post `
        -InFile $packagePath -ContentType "application/octet-stream" -WebSession $session -TimeoutSec 60
    Invoke-Compose $projects[0] $sourceEnvFile @("restart", "erp")
    Wait-ForApplication $sourceUrl
    $sourceContext = Invoke-RestMethod -Uri "$sourceUrl/api/v1/platform/context" -WebSession $session -TimeoutSec 15

    & dotnet build (Join-Path $repositoryRoot "tests\EndToEndTests\Erp.EndToEndTests.csproj") --no-restore
    if ($LASTEXITCODE -ne 0) { throw "The browser test project could not be built." }
    $playwrightInstaller = Join-Path $repositoryRoot "tests\EndToEndTests\bin\Debug\net10.0\playwright.ps1"
    if (-not (Test-Path -LiteralPath $playwrightInstaller)) { throw "The Playwright browser installer is unavailable." }
    & powershell -NoProfile -ExecutionPolicy Bypass -File $playwrightInstaller install chromium
    if ($LASTEXITCODE -ne 0) { throw "Playwright Chromium installation failed." }

    $env:ERP_E2E_BASE_URL = $sourceUrl
    $env:ERP_E2E_EMAIL = $adminEmail
    $env:ERP_E2E_PASSWORD = $adminPassword
    & dotnet test (Join-Path $repositoryRoot "tests\EndToEndTests\Erp.EndToEndTests.csproj") `
        --filter "FullyQualifiedName~CriticalWorkflowTests" --no-restore
    if ($LASTEXITCODE -ne 0) { throw "The synthetic pre-backup browser workflow failed." }

    $sourceTimer.Stop()
    $timings["source_seed_and_workflow_seconds"] = [Math]::Round($sourceTimer.Elapsed.TotalSeconds, 2)

    Invoke-Compose $projects[0] $sourceEnvFile @("stop", "erp")
    $selectedEvent = Invoke-Sql $projects[0] $sourceEnvFile @"
SELECT message."Id"::text || '|' || (message."PayloadJson"::jsonb->>'SalesOrderId')
FROM platform.outbox_messages AS message
WHERE message."EventType" = 'inventory.sales_order.fulfillment_progress.v1'
  AND message."ProcessedAtUtc" IS NOT NULL
  AND message."DeadLetteredAtUtc" IS NULL
ORDER BY message."OccurredAtUtc" DESC, message."Id"
LIMIT 1;
"@
    if ($selectedEvent -notmatch '^([0-9a-fA-F-]{36})\|([0-9a-fA-F-]{36})$') {
        throw "No successfully processed synthetic sales fulfillment event was available for the recovery replay check."
    }
    $replayEventId = $Matches[1]
    $replayOrderId = $Matches[2]
    $replayAuditCount = [int](Invoke-Sql $projects[0] $sourceEnvFile @"
SELECT COUNT(*) FROM platform.audit_logs
WHERE "EntityId" = '$replayOrderId'
  AND "Action" = 'sales.order.fulfillment_updated';
"@)
    if ($replayAuditCount -lt 1) { throw "The source fulfillment transition did not have its expected audit record." }

    $resetEvent = Invoke-Sql $projects[0] $sourceEnvFile @"
UPDATE platform.outbox_messages
SET "ProcessedAtUtc" = NULL, "NextAttemptAtUtc" = CURRENT_TIMESTAMP,
    "LockedUntilUtc" = NULL, "LeaseId" = NULL, "LastError" = NULL
WHERE "Id" = '$replayEventId'::uuid
  AND "ProcessedAtUtc" IS NOT NULL
  AND "DeadLetteredAtUtc" IS NULL
RETURNING "Id";
"@
    if ($resetEvent -ne $replayEventId) {
        throw "The synthetic fulfillment event could not be reset to pending in the disposable source database."
    }

    $backupTimer = [Diagnostics.Stopwatch]::StartNew()
    $sourceDatabase = Get-ContainerId $projects[0] $sourceEnvFile "postgres"
    Invoke-Docker @("exec", $sourceDatabase, "pg_dump", "--username=erp", "--dbname=erp", "--format=custom", "--file=/tmp/erp-recovery.dump")
    $null = New-Item -ItemType Directory -Path `
        (Join-Path $backupDirectory "keys"), (Join-Path $backupDirectory "files"), `
        (Join-Path $backupDirectory "modules"), (Join-Path $backupDirectory "trusted-publishers")
    Invoke-Docker @("cp", "${sourceDatabase}:/tmp/erp-recovery.dump", (Join-Path $backupDirectory "erp.dump"))
    $sourceApplication = Get-ContainerId $projects[0] $sourceEnvFile "erp"
    foreach ($asset in @("keys", "files", "modules", "trusted-publishers")) {
        Invoke-Docker @("cp", "${sourceApplication}:/var/erp/$asset/.", (Join-Path $backupDirectory $asset))
    }
    $backupTimer.Stop()
    $timings["backup_seconds"] = [Math]::Round($backupTimer.Elapsed.TotalSeconds, 2)
    $backupCompletedUtc = [DateTimeOffset]::UtcNow
    $timings["backup_completed_utc"] = $backupCompletedUtc.ToString("O")

    $null = & docker exec $sourceDatabase pg_restore --list /tmp/erp-recovery.dump
    if ($LASTEXITCODE -ne 0) { throw "The synthetic PostgreSQL backup could not be verified." }
    Invoke-Docker @("exec", $sourceDatabase, "sh", "-c", "head -c 64 /tmp/erp-recovery.dump > /tmp/erp-recovery-corrupt.dump")
    & $env:ComSpec /d /c "docker exec $sourceDatabase pg_restore --list /tmp/erp-recovery-corrupt.dump >NUL 2>&1"
    $corruptBackupExitCode = $LASTEXITCODE
    if ($corruptBackupExitCode -eq 0) {
        throw "A truncated synthetic database backup was incorrectly accepted by pg_restore."
    }
    Write-Output "Truncated backup rejection verified."

    $restoreResults = @()
    foreach ($targetIndex in 0, 1) {
        $project = $projects[$targetIndex + 1]
        $environmentFile = if ($targetIndex -eq 0) { $targetAEnvFile } else { $targetBEnvFile }
        $port = if ($targetIndex -eq 0) { $targetAPort } else { $targetBPort }
        $baseUrl = "http://127.0.0.1:$port"
        $restoreTimer = [Diagnostics.Stopwatch]::StartNew()

        Write-Output "Restoring synthetic backup into fresh isolated target $($targetIndex + 1) ($project)."
        $databaseRestoreTimer = [Diagnostics.Stopwatch]::StartNew()
        Invoke-Compose $project $environmentFile @("up", "--detach", "--wait", "postgres")
        Write-Output "Target $($targetIndex + 1): PostgreSQL is ready; restoring the custom-format database archive."
        $targetDatabase = Get-ContainerId $project $environmentFile "postgres"
        Invoke-Docker @("cp", (Join-Path $backupDirectory "erp.dump"), "${targetDatabase}:/tmp/erp-recovery.dump")
        Invoke-Docker @("exec", $targetDatabase, "pg_restore", "--username=erp", "--dbname=erp", "--exit-on-error", "/tmp/erp-recovery.dump")
        $databaseRestoreTimer.Stop()
        $timings["fresh_restore_$($targetIndex + 1)_database_seconds"] = [Math]::Round($databaseRestoreTimer.Elapsed.TotalSeconds, 2)
        Write-Output "Target $($targetIndex + 1): database restore completed."

        $assetRestoreTimer = [Diagnostics.Stopwatch]::StartNew()
        Invoke-Compose $project $environmentFile @("create", "erp")
        $targetApplication = Get-ContainerId $project $environmentFile "erp"
        foreach ($asset in @("keys", "files", "modules", "trusted-publishers")) {
            $assetSource = (Join-Path $backupDirectory $asset) + "\."
            Invoke-Docker @("cp", $assetSource, "${targetApplication}:/var/erp/$asset/")
        }
        $assetRestoreTimer.Stop()
        $timings["fresh_restore_$($targetIndex + 1)_assets_seconds"] = [Math]::Round($assetRestoreTimer.Elapsed.TotalSeconds, 2)
        Write-Output "Target $($targetIndex + 1): persistent application assets restored."

        $applicationValidationTimer = [Diagnostics.Stopwatch]::StartNew()
        Invoke-Compose $project $environmentFile @("up", "--detach", "erp")
        Wait-ForApplication $baseUrl

        $restoredContext = Assert-RecoveredAssets `
            $baseUrl $sourceContext.tenantId.value.ToString() $sourceContext.companyId.value.ToString() $projectSuffix

        $history = Invoke-Sql $project $environmentFile @"
SELECT COUNT(*) FROM inventory."__EFMigrationsHistory"
WHERE "MigrationId" = '20261025032105_FulfillmentLookup';
"@
        if ([int]$history -ne 1) { throw "The restored inventory schema does not record the expected fulfillment lookup migration." }

$replayTimer = [Diagnostics.Stopwatch]::StartNew()
Wait-ForReplay $project $environmentFile $replayEventId
$restoredAuditCount = [int](Invoke-Sql $project $environmentFile @"
SELECT COUNT(*) FROM platform.audit_logs
WHERE "EntityId" = '$replayOrderId'
  AND "Action" = 'sales.order.fulfillment_updated';
"@)
if ($restoredAuditCount -ne $replayAuditCount) {
    throw "Fulfillment event replay changed the restored audit count."
}
$replayTimer.Stop()
$timings["fresh_restore_$($targetIndex + 1)_outbox_replay_seconds"] = [Math]::Round($replayTimer.Elapsed.TotalSeconds, 2)
$applicationValidationTimer.Stop()
$timings["fresh_restore_$($targetIndex + 1)_startup_and_validation_seconds"] = [Math]::Round($applicationValidationTimer.Elapsed.TotalSeconds, 2)

$restoreTimer.Stop()
$timings["fresh_restore_$($targetIndex + 1)_seconds"] = [Math]::Round($restoreTimer.Elapsed.TotalSeconds, 2)
$timings["fresh_restore_$($targetIndex + 1)_backup_age_seconds"] = [Math]::Round(([DateTimeOffset]::UtcNow - $backupCompletedUtc).TotalSeconds, 2)
        $restoreResults += [pscustomobject]@{
            Url = $baseUrl
            Project = $project
            Context = $restoredContext
        }
    }

    if (-not $SkipPostRestoreBrowserWorkflow) {
        $postRestoreWorkflowTimer = [Diagnostics.Stopwatch]::StartNew()
        $env:ERP_E2E_BASE_URL = $restoreResults[0].Url
        $env:ERP_E2E_EMAIL = $adminEmail
        $env:ERP_E2E_PASSWORD = $adminPassword
        & dotnet test (Join-Path $repositoryRoot "tests\EndToEndTests\Erp.EndToEndTests.csproj") `
            --filter "FullyQualifiedName~CriticalWorkflowTests" --no-restore
        if ($LASTEXITCODE -ne 0) { throw "The post-restore browser workflow failed." }
        $postRestoreWorkflowTimer.Stop()
        $timings["post_restore_browser_workflow_seconds"] = [Math]::Round($postRestoreWorkflowTimer.Elapsed.TotalSeconds, 2)
    }

    $timings["total_elapsed_seconds"] = [Math]::Round(([DateTimeOffset]::UtcNow - $startTime).TotalSeconds, 2)
    Write-Output "Recovery rehearsal passed. Synthetic credentials and database/assets were isolated to temporary project volumes."
    $timings.GetEnumerator() | ForEach-Object {
        Write-Output ("{0}={1}" -f $_.Key, $_.Value)
    }
}
finally {
    foreach ($project in $projects) {
        $environmentFile = switch ($project) {
            $projects[0] { $sourceEnvFile; break }
            $projects[1] { $targetAEnvFile; break }
            default { $targetBEnvFile }
        }
        if (Test-Path -LiteralPath $environmentFile) {
            try {
                $labelledVolumes = & docker volume ls --quiet --filter "label=com.docker.compose.project=$project"
                if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace(($labelledVolumes -join ""))) {
                    Invoke-Compose $project $environmentFile @("down", "--volumes", "--remove-orphans")
                }
            }
            catch {
                Write-Warning "Could not remove isolated Docker project '$project'. Inspect it before manual cleanup."
            }
        }
    }

    if ((Test-Path -LiteralPath $workRoot) -and $workRoot.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
    foreach ($key in $environmentKeys) {
        [Environment]::SetEnvironmentVariable($key, $previousEnvironment[$key], "Process")
    }
}
