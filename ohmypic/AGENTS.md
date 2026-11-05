# ohMypic development entry

Read in this order before changing this folder. The linked documents are canonical.

1. `../wiki/ohmypic-plan.md` — phases P0~P5, engine/format table, risks. **To do something the plan does not cover, change the plan first and record why in the run log.**
2. `../wiki/ohmypic-acceptance-tests.md` — OP-* tests for the current phase. Do not start the next phase before they pass.
3. `../wiki/ohmypic-design.md` — only when changing UI. Decision IDs (D01~D45 inherited, P01~P12) go to traceability, not to commit messages or code comments.
4. `../data/ohmypic/traceability.json` — feature → command key → design → tests. `plan_status` only via `build_traceability.py` MAP; `result` only after a test run.
5. Run log: `../runs/2026-11-05-ohmypic/README.md` (commands, exit codes, hashes, captures, failures, recovery).

## Layout

```
ohmypic/
  OhMyPic.sln
  src/OhMyPic.Core/        image engine (WIC), no UI dependency besides System.Windows.Media.Imaging
  src/OhMyPic.App.Text/    UI strings: English neutral .resx + ko satellite
  src/OhMyPic.App/         WPF app. Commands are named by the traceability command key
  tests/OhMyPic.Core.Tests/ xunit. Test names start with the OP-E* ID
  packaging/               package.py (release zip + sha256), README.txt, make_icon.ps1
  dist/                    local copy of the packaged release (generated)
  releases/                package.py output (generated)
```

The public repository `woori-doweb/ohmyzip` holds one folder per app (`ohmyzip/`, `ohmypic/`; root files from `../public-root/`).
Publish committed trees only: `python -X utf8 scripts/publish_apps_repo.py --message "..." --push` from the REA root.
CI `.github/workflows/ohmypic-release.yml` releases `ohmypic-v<major>.<minor>.<run_number>` as a prerelease, never latest
(ohmyZip's updater reads `releases/latest`). Do not rename the workflow file.

## Rules

- Build/test: `dotnet build ohmypic/OhMyPic.sln`, `dotnet test ohmypic/tests/OhMyPic.Core.Tests`. Keep outputs in the run `logs/`; never overwrite a failure log.
- Fixtures: `../data/ohmypic/fixtures/` (or `OHMYPIC_FIXTURES`) are read-only; copy to a temp folder. Do not put fixtures or user photos in this folder.
- Do not write the product, company or executable names of other image viewers anywhere in this folder (code, comments, UI text, docs, file names). Analysis material stays in `../sources/`, `../wiki/`, `../runs/`.
- No string literals in user-facing languages other than English in code; UI text lives in `OhMyPic.App.Text` resources.
- Unimplemented commands stay in the menu, disabled, labelled "(Coming soon)". Never leave a non-working button enabled.
- No third-party DLLs, icons, skins or text images from other products. No SF Pro / SF Symbols.
- Every file write goes through a temp file + atomic replace. Delete originals only after the output was re-decoded.
- After editing docs: `python -X utf8 "$USERPROFILE/.gjc/agent/skills/doc-graph/scripts/docgraph.py" check --store rea-wiki --changed` FAIL 0.
