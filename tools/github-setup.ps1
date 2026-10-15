<#
.SYNOPSIS
  Configures the GitHub repository after the first push: Pages (GitHub Actions source), rulesets that protect main
  and the release tags, repository metadata, and a first run of the site workflow.
.DESCRIPTION
  Needs the GitHub CLI logged in (gh auth login) with the repo and workflow scopes. Idempotent: run it again after
  changing anything here. It never creates the repository or pushes code; do that first:

      gh repo create <owner>/DustPlanner --public --source . --remote origin --push
      tools\github-setup.ps1 -Owner <owner>

  What it does (all through the REST API, nothing else touches the repository):
    - Pages: build_type "workflow", so .github/workflows/pages.yml publishes site/ at https://<owner>.github.io/DustPlanner/
    - Ruleset "main": no deletion, no force-push, linear history on the default branch (direct pushes stay allowed)
    - Ruleset "release tags": exporter-v* tags cannot be deleted or moved once pushed (releases stay reproducible)
    - Repository: description, homepage (the site), topics, issues on, wiki/projects off, delete merged branches
    - Runs the "Publish site" workflow once so the site exists before the first release
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Owner,
    [string]$Repo = "DustPlanner"
)

$ErrorActionPreference = "Stop"
$slug = "$Owner/$Repo"
$site = "https://$($Owner.ToLowerInvariant()).github.io/$Repo/"

function Invoke-Api {
    param([string]$Method, [string]$Path, [object]$Body = $null, [switch]$AllowNotFound)
    # Windows PowerShell 5.1: a body piped to gh picks up the console code page, Set-Content adds a BOM, and with
    # $ErrorActionPreference = Stop any stderr line becomes a terminating error. So: UTF-8 file without BOM, and
    # exit codes checked by hand.
    $ErrorActionPreference = "Continue"
    $ghArgs = @("api", "--method", $Method, $Path, "-H", "Accept: application/vnd.github+json", "-H", "X-GitHub-Api-Version: 2022-11-28")
    $tmp = $null
    if ($null -ne $Body) {
        $tmp = [System.IO.Path]::GetTempFileName()
        $json = ConvertTo-Json -InputObject $Body -Depth 10 -Compress
        [System.IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding $false))
        $ghArgs += @("--input", $tmp)
    }
    try {
        $out = & gh @ghArgs 2>&1 | ForEach-Object { "$_" }
        $text = ($out -join "`n")
        if ($LASTEXITCODE -ne 0) {
            if ($AllowNotFound -and ($text -match "404")) { return $null }
            throw "gh api $Method $Path failed: $text"
        }
        if ($text.Trim() -eq "") { return $null }
        return ($text | ConvertFrom-Json)
    } finally {
        if ($tmp) { Remove-Item $tmp -ErrorAction SilentlyContinue }
    }
}

& { $ErrorActionPreference = "Continue"; gh auth status 2>&1 | Out-Null }
if ($LASTEXITCODE -ne 0) { throw "Run 'gh auth login' first." }
Invoke-Api GET "repos/$slug" | Out-Null
Write-Host "Repository $slug found."

# --- repository metadata -------------------------------------------------------------------------------------------
Invoke-Api PATCH "repos/$slug" @{
    description            = "Read-only dust planner for Hearthstone inside Hearthstone Deck Tracker: missing cards, the least painful disenchant plan, match history, OBS overlay."
    homepage               = $site
    has_issues             = $true
    has_wiki               = $false
    has_projects           = $false
    delete_branch_on_merge = $true
} | Out-Null
Invoke-Api PUT "repos/$slug/topics" @{ names = @("hearthstone", "hearthstone-deck-tracker", "hdt-plugin", "arcane-dust", "dotnet", "csharp", "obs-overlay", "mcp-server") } | Out-Null
Write-Host "Metadata, topics and homepage ($site) set."

# --- GitHub Pages: built and deployed by .github/workflows/pages.yml ------------------------------------------------
$pages = Invoke-Api GET "repos/$slug/pages" -AllowNotFound
if ($null -eq $pages) {
    Invoke-Api POST "repos/$slug/pages" @{ build_type = "workflow" } | Out-Null
    Write-Host "Pages enabled (source: GitHub Actions)."
} elseif ($pages.build_type -ne "workflow") {
    Invoke-Api PUT "repos/$slug/pages" @{ build_type = "workflow" } | Out-Null
    Write-Host "Pages source switched to GitHub Actions."
} else {
    Write-Host "Pages already uses GitHub Actions."
}

# --- rulesets --------------------------------------------------------------------------------------------------------
function Set-Ruleset {
    param([hashtable]$Definition)
    $existing = Invoke-Api GET "repos/$slug/rulesets"
    $match = $existing | Where-Object { $_.name -eq $Definition.name } | Select-Object -First 1
    if ($match) {
        Invoke-Api PUT "repos/$slug/rulesets/$($match.id)" $Definition | Out-Null
        Write-Host "Ruleset '$($Definition.name)' updated."
    } else {
        Invoke-Api POST "repos/$slug/rulesets" $Definition | Out-Null
        Write-Host "Ruleset '$($Definition.name)' created."
    }
}

Set-Ruleset @{
    name        = "main"
    target      = "branch"
    enforcement = "active"
    conditions  = @{ ref_name = @{ include = @("~DEFAULT_BRANCH"); exclude = @() } }
    rules       = @(
        @{ type = "deletion" },
        @{ type = "non_fast_forward" },
        @{ type = "required_linear_history" }
    )
}

Set-Ruleset @{
    name        = "release tags"
    target      = "tag"
    enforcement = "active"
    conditions  = @{ ref_name = @{ include = @("refs/tags/exporter-v*"); exclude = @() } }
    rules       = @(
        @{ type = "deletion" },
        @{ type = "non_fast_forward" },
        @{ type = "update" }
    )
}

# --- first site deployment ------------------------------------------------------------------------------------------
& { $ErrorActionPreference = "Continue"; gh workflow run pages.yml --repo $slug 2>&1 | Out-Null }
if ($LASTEXITCODE -eq 0) { Write-Host "Site workflow started; the page appears at $site in a couple of minutes." }
else { Write-Host "Could not start the site workflow (is .github/workflows/pages.yml on main yet?). It also runs on the next push that touches site/." }

Write-Host ""
Write-Host "Next: bump <Version> in Directory.Build.props when needed, then 'git tag exporter-v<version> && git push origin exporter-v<version>' publishes the release zip."
