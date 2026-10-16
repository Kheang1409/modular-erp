[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ResultsRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function ConvertTo-WorkflowMessage([string] $erpValue) {
    return $erpValue.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
}

function Get-TestCounter([System.Xml.XmlElement] $erpCounters, [string] $erpName) {
    $erpValue = $erpCounters.GetAttribute($erpName)
    if ([string]::IsNullOrEmpty($erpValue)) { return 0L }
    return [long]::Parse($erpValue, [System.Globalization.CultureInfo]::InvariantCulture)
}

if (-not (Test-Path -LiteralPath $ResultsRoot -PathType Container)) {
    Write-Output '::notice::No test result directory was produced.'
    return
}

$erpReports = @(Get-ChildItem -LiteralPath $ResultsRoot -Filter '*.trx' -Recurse -File | Sort-Object FullName)
if ($erpReports.Count -eq 0) {
    Write-Output '::notice::No TRX test reports were produced.'
    return
}

$erpSummary = [System.Collections.Generic.List[string]]::new()
$erpSummary.Add('## Test results')
$erpSummary.Add('')
$erpSummary.Add('| Suite | Total | Executed | Passed | Failed | Not executed |')
$erpSummary.Add('| --- | ---: | ---: | ---: | ---: | ---: |')
$erpFailedNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$erpTotal = 0L
$erpExecuted = 0L
$erpPassed = 0L
$erpFailed = 0L
$erpNotExecuted = 0L

foreach ($erpReport in $erpReports) {
    $erpSettings = [System.Xml.XmlReaderSettings]::new()
    $erpSettings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $erpSettings.XmlResolver = $null
    $erpSettings.MaxCharactersInDocument = 16 * 1024 * 1024
    $erpReader = [System.Xml.XmlReader]::Create($erpReport.FullName, $erpSettings)
    try {
        $erpDocument = [System.Xml.XmlDocument]::new()
        $erpDocument.XmlResolver = $null
        $erpDocument.Load($erpReader)
    }
    finally { $erpReader.Dispose() }

    $erpNamespaces = [System.Xml.XmlNamespaceManager]::new($erpDocument.NameTable)
    $erpNamespaces.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $erpCounters = $erpDocument.SelectSingleNode('/t:TestRun/t:ResultSummary/t:Counters', $erpNamespaces)
    if ($null -eq $erpCounters) { throw 'A TRX report is missing its test result counters.' }
    $erpReportTotal = Get-TestCounter $erpCounters 'total'
    $erpReportExecuted = Get-TestCounter $erpCounters 'executed'
    $erpReportPassed = Get-TestCounter $erpCounters 'passed'
    $erpReportFailed = Get-TestCounter $erpCounters 'failed'
    $erpReportNotExecuted = Get-TestCounter $erpCounters 'notExecuted'
    $erpTotal += $erpReportTotal
    $erpExecuted += $erpReportExecuted
    $erpPassed += $erpReportPassed
    $erpFailed += $erpReportFailed
    $erpNotExecuted += $erpReportNotExecuted
    $erpSuite = $erpReport.Directory.Name.Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ')
    $erpSummary.Add("| $erpSuite | $erpReportTotal | $erpReportExecuted | $erpReportPassed | $erpReportFailed | $erpReportNotExecuted |")

    foreach ($erpResult in $erpDocument.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult[@outcome="Failed"]', $erpNamespaces)) {
        $null = $erpFailedNames.Add($erpResult.GetAttribute('testName'))
    }
}

$erpSummary.Add("| **Total** | **$erpTotal** | **$erpExecuted** | **$erpPassed** | **$erpFailed** | **$erpNotExecuted** |")
foreach ($erpName in ($erpFailedNames | Sort-Object)) {
    # Names only: do not publish exception messages, stack traces, SQL, or test payloads.
    Write-Output "::error title=Failed test::$(ConvertTo-WorkflowMessage $erpName)"
}
if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
    $erpSummary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
}
else { $erpSummary | Write-Output }
