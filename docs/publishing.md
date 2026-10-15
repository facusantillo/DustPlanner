# Publishing on GitHub

How the project reaches people: the repository, CI, the release zip, and the site. Everything below is driven by
files already in the repository; the only manual steps are creating the repository and running one script.

## 1. Create the repository and push

```powershell
gh auth login                                   # once; repo + workflow scopes
gh repo create <owner>/DustPlanner --public --source . --remote origin --push
```

(or create it in the web UI, then `git remote add origin … && git push -u origin main`). `ci.yml` runs on every
push to `main`: it fetches the HDT 1.55.6 reference assemblies from GitHub, builds, runs the tests and the HDT API
check, and validates the data files.

## 2. Configure the repository

```powershell
tools\github-setup.ps1 -Owner <owner>
```

Idempotent; needs the GitHub CLI. It sets, through the REST API:

| What | Setting |
|---|---|
| GitHub Pages | source **GitHub Actions**: `.github/workflows/pages.yml` publishes `site/` at `https://<owner>.github.io/DustPlanner/` on every push that touches it |
| Ruleset `main` | the default branch cannot be deleted or force-pushed and keeps a linear history; direct pushes stay allowed (one maintainer) |
| Ruleset `release tags` | `exporter-v*` tags cannot be deleted or moved: a published release always points at the commit it was built from |
| Repository | description, homepage = the site, topics, issues on, wiki and projects off, merged branches deleted |
| Site | runs the `Publish site` workflow once |

The same calls by hand (any OS):

```bash
gh api -X POST repos/<owner>/DustPlanner/pages -f build_type=workflow
gh api -X POST repos/<owner>/DustPlanner/rulesets --input - <<'JSON'
{"name":"main","target":"branch","enforcement":"active",
 "conditions":{"ref_name":{"include":["~DEFAULT_BRANCH"],"exclude":[]}},
 "rules":[{"type":"deletion"},{"type":"non_fast_forward"},{"type":"required_linear_history"}]}
JSON
gh api -X POST repos/<owner>/DustPlanner/rulesets --input - <<'JSON'
{"name":"release tags","target":"tag","enforcement":"active",
 "conditions":{"ref_name":{"include":["refs/tags/exporter-v*"],"exclude":[]}},
 "rules":[{"type":"deletion"},{"type":"non_fast_forward"},{"type":"update"}]}
JSON
gh workflow run pages.yml
```

## 3. Publish a release

The version is `<Version>` in `Directory.Build.props` (plugin assembly version, zip name, panel's service version).

```powershell
# bump <Version>, commit, then:
git tag exporter-v0.1.0
git push origin exporter-v0.1.0
```

`release.yml` builds the plugin, publishes the service self-contained (win-x64), packs `DustPlanner-v0.1.0.zip` with
its `.sha256`, and creates the GitHub Release. The site's download button and the README link to `releases/latest`.

## 4. Afterwards

- **Signing**: the service executable is not code-signed; SmartScreen may warn on first run. The site says so. A
  code-signing certificate removes the warning but costs money; most small open-source tools ship unsigned.
- **HDT plugin list**: HearthSim keeps a community plugin list in the Hearthstone Deck Tracker GitHub wiki; a pull
  request there pointing at the release and the site is how other HDT users find plugins.
- **Update notice**: not implemented. The service could ask `api.github.com/repos/<owner>/DustPlanner/releases/latest`
  once a day and show "a newer version exists" on the panel's Home page.
- **Issues**: a bug template asking for `%LocalAppData%\DustPlanner\exporter.log`, the HDT version and the plan output
  saves a round trip.
