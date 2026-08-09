# App Center

Ubuntu's App Center, rebuilt for Windows on top of **winget**. WPF on .NET 10,
no external packages — the whole thing compiles against what ships with the SDK.

Free software under the [GPL-3.0](LICENSE), the same licence Canonical give
[Ubuntu's App Center](https://github.com/ubuntu/app-center) — see
[Licence and origins](#licence-and-origins).

![Explore](docs/explore.png)

## Running it

```
dotnet run --project AppCenter.csproj
```

Or build once and launch the exe:

```
dotnet build -c Release
bin\Release\net10.0-windows\AppCenter.exe
```

Requires the Windows Package Manager (`winget`). It ships with App Installer on
Windows 11; if `winget --version` fails, install App Installer from the
Microsoft Store. The About page reports whether it was found.

## Releasing

```
deploy.bat            builds the version in AppCenter.csproj
deploy.bat 1.1.0      builds that version instead
```

Everything lands in `releases\`:

| | |
| --- | --- |
| `AppCenter-<v>-Setup.exe` | Inno Setup installer, per-user, no UAC |
| `AppCenter-<v>-portable.zip` | the publish folder, unpacked and run anywhere |
| `winget\<v>\*.yaml` | the three manifests, hash and version already filled in |

The build is framework-dependent, so the installer looks for the .NET 10 Desktop
Runtime and downloads it from `aka.ms` if it is missing — that is the one case
where a UAC prompt appears, since the runtime installs machine-wide. Everything
else goes into `%LOCALAPPDATA%\Programs\AppCenter`.

`deploy.bat` installs Inno Setup itself (via winget, naturally) the first time it
runs. Version, publisher, licence and project URL come from the config block at
the top of the script and from `<Version>` in `AppCenter.csproj`.

To ship a version: run `deploy.bat`, attach the setup exe to a release under that
same URL, then open a PR on
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) with the
`releases\winget\<v>` folder dropped into `manifests\a\<Publisher>\AppCenter\<v>`.
`winget validate --manifest releases\winget\<v>` checks them before you do.

## What it does

| Page | Backed by |
| --- | --- |
| Explore | `catalog.json` — curated banner + picks |
| Featured / Productivity / Development | `catalog.json` sections, with sorting |
| Games | Curated carousel + Top Rated grid |
| Search | Live `winget search`, enriched with `winget show` |
| App detail | `winget show` + `winget list` for install state |
| Manage | Live `winget upgrade` and `winget list` |

Install, update and uninstall run **real winget commands against this machine**.
Every one of them goes through a confirmation dialog first, and Windows itself
raises the UAC prompt when an installer needs elevation. Nothing is executed
without an explicit click.

## Layout

```
AppCenter.csproj          net10.0-windows, UseWPF, zero NuGet dependencies
catalog.json              the curated catalogue (edit this to change the pages)
app.manifest              per-monitor v2 DPI awareness
AppCenter.ico             the app icon, 16-256 (see tools/make_icon.py)
LICENSE                   GPL-3.0; ships next to the exe, About links to it
deploy.bat                publish + installer + winget manifests -> releases/

installer/
  AppCenter.iss           Inno Setup script, incl. the .NET runtime bootstrap
  winget/template.*.yaml  manifest templates deploy.bat fills in

Themes/
  Palette.xaml            Yaru dark colours, fonts, banner gradient
  Icons.xaml              24x24 stroke geometries for every icon
  Controls.xaml           nav items, buttons, search box, combo box, switch
  Templates.xaml          app card + Manage row templates

Models/AppPackage.cs      one package; notifies so late-arriving data lands
Services/
  WingetService.cs        async wrapper + fixed-width table parser
  CatalogService.cs       loads catalog.json
  IconService.cs          favicon fetch + disk cache
Controls/
  AppGrid.xaml            the shared two-column card grid
  ConfirmDialog.xaml      dark confirmation prompt
  Nav.cs, Converters.cs   sidebar badge plumbing
Views/                    one file per page, all deriving from PageView
tools/make_icon.py        redraws the app icon (needs Pillow + numpy)
```

## The app icon

<img src="docs/icon.png" width="128" align="right" />

A shopping bag with an install arrow, on the same indigo-to-green ramp the
Explore banner uses, with the Yaru accent orange on the arrow — so the icon and
the app's first screen are visibly the same thing.

It's drawn rather than stored: `python tools/make_icon.py` renders the artwork on
a 1024x1024 grid at 4x supersample and downsamples into all ten sizes (16-256),
writing `AppCenter.ico` and `docs/icon.png`. Colours are the literals from
`Themes/Palette.xaml`, so re-theming the app and re-running the script keeps them
in step. Pillow and numpy are needed for that script only — the app itself still
builds with no dependencies beyond the SDK.

## Icons for catalogue apps

winget carries no icon data, so icons come **from each app's own homepage**, and
are cached in `%LOCALAPPDATA%\AppCenter\icons`. No third-party icon service is
contacted. Resolution runs in this order:

1. an explicit `"icon"` URL in the `catalog.json` entry, if there is one;
2. otherwise the homepage is read and whatever it declares in
   `<link rel="apple-touch-icon">` or `<link rel="icon">` is used, best first —
   apple-touch-icon ahead of favicon, larger ahead of smaller;
3. failing that, the conventional paths at the site root
   (`/apple-touch-icon.png`, `/favicon.ico`, …).

The order matters. Guessing at the site root first is what makes
`https://www.mozilla.org` hand back the Mozilla flag instead of Firefox's logo:
the root of a site is the *company*, and the product icon lives on the product's
own page. Reading the page's own declaration gets Chrome, Edge and Firefox right
without pinning anything.

Code hosts — `github.com`, `gitlab.com`, `sourceforge.net`, `codeberg.org` and
friends — are **skipped entirely**, because every project on them shares one set
of page furniture and therefore one icon. Left alone, a `github.com/user/repo`
homepage puts the Octocat on two dozen unrelated cards, each claiming to be
GitHub. A letter tile says more than that. Subdomains are not matched, so
`desktop.github.com` still gets GitHub Desktop's own icon, which is correct.

Anything that yields nothing falls back to a generated letter tile whose colour
is derived from the package id, so it stays stable between runs. After six
consecutive network failures the app stops trying and runs on letter tiles.

To pin an image, add an `"icon"` URL to the `catalog.json` entry — it takes
priority over everything above, and it is the answer for projects that live on a
code host and have no site of their own. Note that entries are per-section, so
an app appearing on several pages needs the field on each of them.

The cache filename carries a version (`<id>.v2.png`). Bump `CacheSuffix` in
`IconService` when the rules change — without that, a fix never reaches anyone
who already ran the app, because the wrong icon is on disk and gets returned
before any of the new logic runs.

## Screenshots in the Games carousel

A carousel entry with a `"screenshot"` URL shows it behind the title, under a
gradient scrim; one without keeps its generated colour panel, so the strip reads
as a set of distinct slides either way. The two peek panels either side show the
neighbouring slides' images too.

```json
{
  "id": "Xonotic.Xonotic",
  "screenshot": "https://xonotic.org/static/img/carousel_maps.jpg",
  "name": "Xonotic",
  "summary": "A fast open source arena shooter"
}
```

Unlike icons there is no guessing: nothing about a URL says where a project keeps
its screenshots, and an `og:image` is as likely to be a logo as a game. Every one
is picked by hand, from the project's own site, and only the URL is stored — no
images are redistributed with App Center. They are fetched once, decoded down to
960px on the way in (a 1920x1080 press shot costs 8MB of bitmap held at source
size, for a 250px-tall slide) and cached under `%LOCALAPPDATA%\AppCenter\screenshots`.

## Editing the catalogue

`catalog.json` is copied next to the exe on build. It currently carries 178
distinct apps across the six sections, spanning browsers, office, creative,
audio and video, developer tooling, system utilities, science and CAD, and
games. Each entry needs a real winget package id; `badge` is `"verified"`
(green tick), `"star"` (orange), or omitted. Every id in the file has been
checked to resolve — to confirm one yourself:

```
winget search --id <Package.Id> --exact --source winget
```

An id that does not resolve still renders a card, but installing it fails, so
it is worth running that check before committing a new entry. Sections may
share apps freely; the same id appears on several pages by design.

## Notes on the winget layer

`winget` has no machine-readable output for `search`/`list`, so `WingetService`
parses the fixed-width table: it finds the dashed separator, reads the header
above it for column offsets, and slices rows at those offsets. That keeps names
containing spaces intact and tolerates columns being reordered or absent
(`Available` and `Match` only appear sometimes). Lookups are by English header
name with a positional fallback.

## Licence and origins

App Center is free software under the [GNU General Public License v3.0](LICENSE).
Use it, read it, change it, pass it on — anything you pass on carries the same
freedoms.

The licence is not an accident of habit. This app owes its whole shape to
[Ubuntu's App Center](https://github.com/ubuntu/app-center): the sidebar, the
banner, the card grid, the split between a curated catalogue and live search.
Canonical publish that under the GPL-3.0, and taking something more permissive
for a reimplementation of their design would be helping yourself to the idea
while giving less back than they did.

No code is shared between the two. Theirs is Dart and Flutter over snapd; this
is C# and WPF over winget, written from the screenshots outwards. **App Center
for Windows is not affiliated with, sponsored by, or endorsed by Canonical
Ltd.** "Ubuntu" and "Canonical" are trademarks of Canonical Ltd, used here only
to say truthfully what this was modelled on.

The Yaru palette in `Themes/Palette.xaml` is Ubuntu's, taken from the
[Yaru theme](https://github.com/ubuntu/yaru) (GPL-3.0 / CC-BY-SA-4.0).

Distributing a build means passing on the source it was built from — the GPL
asks for that, and `LICENSE` ships inside the installer and the portable zip so
the terms travel with the binary.
