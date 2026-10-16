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
tools\github-setup.ps1 -Owner <owner> [-Domain <domain>]
```

Idempotent; needs the GitHub CLI. It sets, through the REST API:

| What | Setting |
|---|---|
| GitHub Pages | source **GitHub Actions**: `.github/workflows/pages.yml` publishes `site/` on every push that touches it, at `https://<owner>.github.io/DustPlanner/` or at the custom domain given with `-Domain` |
| Ruleset `main` | the default branch cannot be deleted or force-pushed and keeps a linear history; direct pushes stay allowed (one maintainer) |
| Ruleset `release tags` | `v*` tags cannot be deleted or moved: a published release always points at the commit it was built from |
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
 "conditions":{"ref_name":{"include":["refs/tags/v*"],"exclude":[]}},
 "rules":[{"type":"deletion"},{"type":"non_fast_forward"},{"type":"update"}]}
JSON
gh workflow run pages.yml
```

### Custom domain: dustplanner.app

The site is served at **https://dustplanner.app/** (domain registered with Cloudflare, site still hosted by GitHub
Pages). DNS records in Cloudflare (*dustplanner.app → DNS → Records*), all **DNS only** (grey cloud: with the proxy
on, GitHub cannot issue the certificate):

| Type | Name | Content |
|---|---|---|
| A | `@` | `185.199.108.153` |
| A | `@` | `185.199.109.153` |
| A | `@` | `185.199.110.153` |
| A | `@` | `185.199.111.153` |
| AAAA | `@` | `2606:50c0:8000::153` |
| AAAA | `@` | `2606:50c0:8001::153` |
| AAAA | `@` | `2606:50c0:8002::153` |
| AAAA | `@` | `2606:50c0:8003::153` |
| CNAME | `www` | `facusantillo.github.io` |

Then `tools\github-setup.ps1 -Owner facusantillo -Domain dustplanner.app` sets the Pages custom domain and the
repository homepage. `.app` is HTTPS-only in every browser (HSTS preload), so the site answers only once GitHub has
issued the certificate: run the script again after a few minutes to enforce HTTPS. To keep anyone else from pointing
a GitHub Pages site at the domain, verify it once in the account settings (*Settings → Pages → Add a verified
domain*, one TXT record). The old `facusantillo.github.io/DustPlanner/` address redirects to the domain.

## 3. Publish a release

The version is `<Version>` in `Directory.Build.props` (plugin assembly version, zip name, panel's service version).

```powershell
# bump <Version>, commit, then:
git tag v1.0.0
git push origin v1.0.0
```

`release.yml` builds the plugin, publishes the service self-contained (win-x64), packs `DustPlanner-v1.0.0.zip` with
its `.sha256`, adds the same zip as `DustPlanner.zip` (fixed name) and creates the GitHub Release. The site's download
button and the README point at `releases/latest/download/DustPlanner.zip`, which always serves the newest release
directly; the versioned name is for the releases page and for keeping older versions apart. (The first, pre-site
release was tagged `exporter-v0.1.0`; the ruleset still protects that pattern.)

### If a release tag has to move

The `release tags` ruleset blocks deleting or moving `v*` tags, maintainers included, so a tag pushed at a
commit whose workflow failed cannot simply be re-pointed. Either release the next patch version, or lift the ruleset
for the one operation and put it back:

```powershell
$id = gh api repos/<owner>/DustPlanner/rulesets --jq '.[] | select(.name == "release tags") | .id'
gh api -X PUT repos/<owner>/DustPlanner/rulesets/$id -f enforcement=disabled
git push origin --delete v1.0.0
git tag -d v1.0.0
git tag v1.0.0 && git push origin v1.0.0
tools\github-setup.ps1 -Owner <owner>          # re-activates the ruleset (idempotent)
```

### Notes for the workflows

- The shell scripts under `tools/` are run as `bash tools/<script>.sh` in the workflows: commits made from Windows
  do not carry the executable bit, and a bare `tools/<script>.sh` fails with exit code 126 ("Permission denied").
- Jobs run on `ubuntu-24.04` (named image, not `ubuntu-latest`) so a runner-image migration cannot change a build.
- Action majors are the Node 24 ones (`checkout@v5`, `setup-dotnet@v5`, `cache@v5`, `upload-artifact@v5`,
  `configure-pages@v6`, `upload-pages-artifact@v5`, `deploy-pages@v5`); older majors print a Node 20 deprecation.

## 4. Afterwards

- **Signing**: the service executable is not code-signed; SmartScreen may warn on first run. The site says so. A
  code-signing certificate removes the warning but costs money; most small open-source tools ship unsigned.
- **HDT plugin list**: HearthSim keeps a community plugin list in the Hearthstone Deck Tracker GitHub wiki; a pull
  request there pointing at the release and the site is how other HDT users find plugins.
- **Update notice**: not implemented. The service could ask `api.github.com/repos/<owner>/DustPlanner/releases/latest`
  once a day and show "a newer version exists" on the panel's Home page.
- **Issues**: a bug template asking for `%LocalAppData%\DustPlanner\exporter.log`, the HDT version and the plan output
  saves a round trip.
