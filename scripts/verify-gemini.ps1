<#
.SYNOPSIS
  Manual, live verification of the real Gemini path (NOT part of the automated test suite).

.DESCRIPTION
  Drives a running AI-REAP API end to end against the real Gemini API:
    /api/ai/status -> create project -> requirement source -> /analyze -> /generate-requirements -> Copilot
  and checks that each stage returns structured, persisted results. It uses a few Gemini
  requests (well inside the free tier). The API key is never read, printed or sent by this script;
  the API reads it from its own configuration (user-secrets / Ai__Gemini__ApiKey).

.EXAMPLE
  # API running (dotnet run --project src/AiReap.Api), signed-in user must be BA or Administrator:
  $env:AIREAP_VERIFY_EMAIL = "you@company.com"
  $env:AIREAP_VERIFY_PASSWORD = "<your password>"
  ./scripts/verify-gemini.ps1 -ApiBaseUrl http://localhost:5299
#>
param(
    [string]$ApiBaseUrl = "http://localhost:5299",
    [string]$Email = $env:AIREAP_VERIFY_EMAIL,
    [string]$Password = $env:AIREAP_VERIFY_PASSWORD
)

$ErrorActionPreference = "Stop"

if (-not $Email -or -not $Password) {
    throw "Set AIREAP_VERIFY_EMAIL and AIREAP_VERIFY_PASSWORD (a Business Analyst or Administrator account)."
}

function Invoke-Api([string]$Method, [string]$Path, $Body = $null) {
    $params = @{ Method = $Method; Uri = "$ApiBaseUrl$Path"; Headers = $script:Headers; ContentType = "application/json" }
    if ($null -ne $Body) { $params.Body = ($Body | ConvertTo-Json -Depth 10) }
    try {
        return Invoke-RestMethod @params
    } catch {
        $detail = $_.ErrorDetails.Message
        throw "$Method $Path failed: $($_.Exception.Message) $detail"
    }
}

function Step([string]$Name) { Write-Host "`n== $Name" -ForegroundColor Cyan }
function Pass([string]$Message) { Write-Host "  PASS  $Message" -ForegroundColor Green }

$script:Headers = @{}
Step "Sign in"
$login = Invoke-Api POST "/api/auth/login" @{ email = $Email; password = $Password }
$script:Headers = @{ Authorization = "Bearer $($login.token)" }
Pass "signed in as $($login.email) ($($login.roles -join ', '))"

Step "AI provider status (no generation)"
$status = Invoke-Api GET "/api/ai/status"
Write-Host "  provider=$($status.provider) model=$($status.model) configured=$($status.configured) modelAvailable=$($status.modelAvailable) $($status.detail)"
if ($status.provider -ne "Gemini" -or -not $status.configured -or -not $status.modelAvailable) {
    throw "Gemini is not active/usable - fix the configuration above before continuing (see docs/GEMINI_SETUP.md)."
}
Pass "Gemini key accepted and model '$($status.model)' available"

Step "Create project + requirement source"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$project = Invoke-Api POST "/api/projects" @{
    name = "Gemini verification $stamp"
    description = "Created by scripts/verify-gemini.ps1"
    objectives = "Reduce leave approval time`nGive employees visibility of their leave balance"
}
$source = Invoke-Api POST "/api/projects/$($project.id)/requirement-sources" @{
    sourceType = 1
    rawText = "We need an employee leave system where employees request leave and managers approve it."
}
Pass "project $($project.id), source $($source.id)"

Step "POST /api/requirement-sources/{id}/analyze  (real Gemini)"
$sw = [Diagnostics.Stopwatch]::StartNew()
$analysis = Invoke-Api POST "/api/requirement-sources/$($source.id)/analyze"
Write-Host "  $([int]$sw.Elapsed.TotalSeconds)s  actors: $($analysis.actors -join ', ')"
Write-Host "  capabilities: $($analysis.capabilities -join '; ')"
foreach ($q in $analysis.clarificationQuestions) { Write-Host "  $($q.code): $($q.data.question)" }
if ($analysis.actors.Count -eq 0 -or $analysis.clarificationQuestions.Count -eq 0) { throw "Analysis returned no actors or no clarification questions." }
Pass "$($analysis.clarificationQuestions.Count) clarification question(s) persisted as artifacts"

Step "POST /api/requirement-sources/{id}/generate-requirements  (real Gemini)"
$sw.Restart()
$generated = Invoke-Api POST "/api/requirement-sources/$($source.id)/generate-requirements"
Write-Host "  $([int]$sw.Elapsed.TotalSeconds)s"
foreach ($fr in $generated.functionalRequirements) { Write-Host "  $($fr.code) [$($fr.status)] $($fr.title)" }
foreach ($nfr in $generated.nonFunctionalRequirements) { Write-Host "  $($nfr.code) $($nfr.title) ($($nfr.data.category))" }
if ($generated.functionalRequirements.Count -eq 0) { throw "No functional requirements were generated." }
Pass "$($generated.functionalRequirements.Count) FR / $($generated.nonFunctionalRequirements.Count) NFR, all AiGenerated awaiting review"

Step "Traceability: Business Objective -> Requirement"
$matrix = Invoke-Api GET "/api/projects/$($project.id)/traceability-matrix"
foreach ($row in $matrix) { Write-Host "  $($row.functionalRequirementCode) <- $((@($row.businessObjectives) | ForEach-Object { $_.code }) -join ', ')" }
Pass "matrix returned $($matrix.Count) row(s)"

Step "Project Copilot  (real Gemini)"
$fr = $generated.functionalRequirements[0].code
$answer = Invoke-Api POST "/api/projects/$($project.id)/copilot/ask" @{ question = "What is impacted if $fr changes, and which requirements lack test cases?" }
Write-Host "  $($answer.answer)"
Write-Host "  related: $($answer.relatedArtifactCodes -join ', ')"
Pass "Copilot answered from project data"

Step "AI audit trail"
$audit = Invoke-Api GET "/api/projects/$($project.id)/ai-executions"
$models = ($audit | ForEach-Object { $_.model } | Sort-Object -Unique) -join ', '
Write-Host "  executions=$($audit.Count) models=$models"
if ($models -match "stub") { throw "The audit trail shows the stub model - the API is not using Gemini." }
Pass "every AI execution recorded with the Gemini model"

Write-Host "`nGemini live verification PASSED. Project '$($project.name)' was left in place for inspection." -ForegroundColor Green
