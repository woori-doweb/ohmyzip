# ohmy apps

Small Windows desktop apps built with WPF on .NET 9. English is the default UI language; Korean is included.

| Folder | App | What it does | Releases |
|---|---|---|---|
| [`ohmyzip/`](ohmyzip/) | **ohmyZip** | Archive manager: open, extract, test and create archives | tags `v*` (the latest release) |
| [`ohmypic/`](ohmypic/) | **ohMypic** | Image browser and viewer: thumbnails, viewer, slideshow, rotate/resize/convert/batch edit | tags `ohmypic-v*` (prereleases) |

Each folder is a self-contained solution (`OhMyZip.sln`, `OhMyPic.sln`) with its own `packaging/` and `AGENTS.md`.
A push to `main` that changes a folder builds it, runs its self-contained tests and publishes a GitHub Release
(`.github/workflows/release.yml` for ohmyZip, `.github/workflows/ohmypic-release.yml` for ohMypic).
Both apps update themselves from these releases; ohMypic releases are never marked latest, so ohmyZip's
updater keeps reading its own releases.

## Requirements

Windows 10 1809+ or Windows 11 (x64) and the .NET Desktop Runtime 9 (x64). Download the zip from
[Releases](../../releases), extract it and run `ohmyZip.exe` or `ohMypic.exe`. There is no installer or code signature yet.
