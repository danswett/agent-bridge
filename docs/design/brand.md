# Slipgates brand

> **Brand spec: rename approved by the owner 2026-11-03. Every item first marked OPEN was
> decided by the owner the same day (§11).** Everything on this page is approved and is the brand. [ui.md](ui.md) still governs the
> states, their meaning, copy and behaviour. This page governs how the brand looks and what it is
> called. Where the two touch (the status glyphs and the app icon), this page gives the drawing
> and ui.md gives the meaning.

Issues: [#535](https://github.com/danswett/agent-bridge/issues/535) (the rename epic) and
[#536](https://github.com/danswett/agent-bridge/issues/536) (this spec). Each surface adopts the
brand in its own issue: [#537](https://github.com/danswett/agent-bridge/issues/537) (Windows),
[#538](https://github.com/danswett/agent-bridge/issues/538) (PWA),
[#539](https://github.com/danswett/agent-bridge/issues/539) (docs) and
[#260](https://github.com/danswett/agent-bridge/issues/260) (Apple). Code identifiers are renamed
in [#541](https://github.com/danswett/agent-bridge/issues/541), and nowhere else.

The boards are in [proposals/536-slipgates-brand/](proposals/536-slipgates-brand/), with all
eleven on one [contact sheet](proposals/536-slipgates-brand/contact-sheet.png). They are drawn
from `design/canvas/Proposal-536-*.dc.html` by
[tools/design-mockups](../../tools/design-mockups/README.md), from the same generated tokens,
glyphs and icons the clients use. The demo machines are ULTRA and DEV-VM1. Every session,
time and path on them is made up.

## 1. What the name means

Every agent and terminal, and every machine and device it reaches, is a **slipgate**: a place
you can step through to somewhere else at once. Each gate is linked to the others. A question
asked on a Dev Box shows up on your phone, and your answer goes straight back. **Slipgates** is
the name for all of them together: instant passage between every agent, every machine and you.

The mark draws one link: two identical gates, one turned to face the other, joined into an S
(§4).

The idea guides the name, the mark and how the product is introduced (a README, a store
listing, an About page). It stays out of the product's working words. A status, a button or an
error says what is happening in plain language: "Working", "Offline", "Can't reach the relay",
never "the gate is closed". Clarity in UI copy always beats the metaphor (§3, N1 and N2). No
mythology or flavour text appears in the product UI.

## 2. The name

- **Slipgates.** One word, a capital S, and the rest lower case. It is always plural and never
  possessive in a title.
  - Don't write SlipGates, SLIPGATES (except where a platform sets all caps itself), Slip
    Gates, Slip-gates, slipgates (except in code identifiers and addresses), or "the
    Slipgates".
  - In running text it takes a singular verb, because it is the product's name: "Slipgates
    shows who needs you."
- **Short name:** also "Slipgates" (9 characters). It fits every home-screen and launcher label,
  so there is no abbreviation. Don't write SG or Slip.
- **Support email:** `support@slipgates.com`.
- **Addresses:** the PWA will be served at `app.slipgates.com` and the relay at
  `relay.slipgates.com`. Neither is live yet: #393 and #540 move them. Until then, no
  surface may show or link either address.
- **A component's name** is "Slipgates" followed by a plain noun in lower case: "the
  Slipgates relay", "the Slipgates app for Windows". Only the product name is capitalised.

### The brand token: one source for the name

The product's name was typed by hand in about 25 places across the Windows app, the host, the
PWA and the Apple clients, and about 30 tests pinned that text. `design/tokens.json` `brand` is
now the one source. `tools/design-gen` writes it into each client:

| Client | Generated file | Names |
|---|---|---|
| Windows app (XAML) | `src/AgentBridge.App/Design/DesignResources.xaml` | `AbBrandName`, `AbBrandShortName`, `AbBrandSupportEmail` (`x:String`) |
| Windows app and host (C#) | `src/AgentBridge.Protocol/DesignBrand.cs` | `DesignBrand.Name`, `.ShortName`, `.SupportEmail` |
| PWA | `pwa/design/brand.js` | `BRAND.name`, `.shortName`, `.supportEmail` |
| Apple | `design/apple/Brand.swift` | `Brand.name`, `.shortName`, `.supportEmail` |
| Android | `design/android/values/brand.xml` | `@string/brand_name`, `brand_short_name`, `brand_support_email` (`translatable="false"`) |

Nothing reads these yet. #537, #538 and #260 replace each hand-typed "Agent Bridge" with them,
and switch tests from the literal to the constant. After that, renaming the product again is one
token change. Code identifiers (namespaces, `Ab*` and `--ab-*` prefixes, the generated files'
paths) keep their current names until #541.

## 3. Naming map

These are the names a person sees. **Approved** means it follows directly from the rename.
**Decided** means it was a judgement call, which the owner decided on 2026-11-03. §3.2 keeps
the options that were weighed, as a record.

### 3.1 Product and components

| Today | Where | Slipgates | Status |
|---|---|---|---|
| Agent Bridge (the product) | everywhere | **Slipgates** | Approved |
| Agent Bridge (Windows app): window title and title bar, Start, Alt+Tab, Task Manager | `MainWindow`, the csproj (no `Product` today, so Task Manager shows "AgentBridge") | **Slipgates**. Add `Product` and `AssemblyTitle` "Slipgates" (#537) | Approved |
| Windows store or download listing | releases, README | **Slipgates for Windows**, only in a listing that names several platforms. In the app, just Slipgates | Decided N4 |
| Tray tooltip "Agent Bridge · 3 need you" (ui.md §7) | `TrayIcon`, `App.xaml.cs` | **Slipgates · 3 need you** | Approved |
| Tray and menu-bar items "Open Agent Bridge", "Quit Agent Bridge", "About Agent Bridge" | Windows tray, macOS menu bar, Apple About | **Open Slipgates**, **Quit Slipgates**, **About Slipgates** | Approved |
| Notification sender (toast `DisplayName`, Web Push and Apple fallback title) | `Toasts`, `ToastResponder`, `sw.js`, `BridgeNotificationPlanner` | **Slipgates**. The toast AUMID and every notification ID stay | Approved |
| Update toast "Agent Bridge updated to {v}" | Windows | **Slipgates updated to {v}** | Approved |
| Installer and release titles: vpk `packTitle` "Agent Bridge", "Agent Bridge v…" | `build/pack-app.ps1`, `publish-prerelease.ps1` | **Slipgates** and **Slipgates {version}**, from one `$ProductName` beside the packId. The packId and the setup file's name stay (#541) | Approved |
| "Bridge Host", the host service on each machine (about 15 strings, plus docs) | Settings, link state, fleet, deliveries, connection errors, host messages | **the Slipgates host** on first mention, then **the host** | Decided N1 |
| Settings section "Bridge Host on this machine" | Windows Settings | **Host on this machine** | Decided N1 |
| "the Agent Bridge app…" in host messages | `LaunchService`, `CallerGuard`, `LocalApi` | **the Slipgates app** | Approved |
| Relay root page "Agent Bridge relay" | relay | **Slipgates relay** | Approved |
| PWA: `<title>`, `apple-mobile-web-app-title`, manifest `name` and `short_name`, `aria-label` "Agent Bridge: sessions", the nav label, `.brand-name` | `pwa/index.html`, `manifest.webmanifest` | **Slipgates** (`name` and `short_name` both). The aria-label becomes "Slipgates: sessions" | Approved |
| The PWA, in docs and copy | README, sign-in | **the Slipgates web app**. "PWA" stays an engineering word | Approved |
| "doesn't support Agent Bridge sessions yet" (3 copies), sign-in and empty-machine copy, notification permission and blocked copy | PWA, Apple | The same sentences with **Slipgates**, each kept as one shared string (#538) | Approved |
| Device name suffix: "{device} · Agent Bridge" (PWA), "{machine} · Agent Bridge app" (Windows), "Apple client" (Apple) | `identity.js`, `App.xaml.cs`, `BridgeStore.swift` | **{device} · Slipgates** on every client, from the brand token | Decided N5 |
| Task Scheduler task description | `HostManager` | **Slipgates host for {profile}**. The task's name stays | Approved |
| macOS and iOS `CFBundleDisplayName` "Agent Bridge" | `apple/project.yml` | **Slipgates**. Bundle IDs stay | Approved |
| "Connect to Agent Bridge" (Apple empty state) | `SessionWorkbench` | **Connect to Slipgates** | Approved |
| GitHub App "Agent Bridge (dev)", shown at sign-in | GitHub | **Slipgates (dev)**. Its client id stays (#539) | Approved |
| Repository README title | `README.md` | **Slipgates**, with "(formerly Agent Bridge)" under the title until #541 renames the repository | Approved (#539) |
| Home Assistant device and entity names: "AI Agent Bridge ({machine})" and others | `HaContract` | **Deferred.** They are ported from agent-ha-bridge and some HA IDs derive from them. Rename them in one coordinated HA change | Deferred |
| Bundled monospace "Agent Bridge Mono" | `design/fonts/slipgates-mono` | **Slipgates Mono**, beside **Slipgates Sans** (#544) | Done in #557 (N3) |
| The words for the things the product links: machine, device, agent, session, delegate, relay | ui.md §8 and every client | Unchanged plain words. "Gate" never appears in status or error copy | Decided N2 |

### 3.2 Naming decisions (decided 2026-11-03)

Board: [proposal-536-decision-naming.png](proposals/536-slipgates-brand/proposal-536-decision-naming.png).
The owner chose option A in each. The other options are kept as a record of what was weighed.

- **N1. "Bridge Host".** It is the service on each machine that runs and watches its agents.
  - **A (decided): "the Slipgates host"** where it first appears in a screen or a page,
    then "the host". The Settings section is "Host on this machine". Errors say "The host isn't
    running" and "Can't reach the host on ULTRA". It is a plain word people already understand,
    and the product name says whose host it is.
  - B: "the gate on this machine", or "this machine's gate", with "Gate on this machine" in
    Settings. It is the slipgate idea at its most literal, but "The gate isn't running" makes a
    person stop and translate. Plain-language alternative: A.
  - C: "Slipgates service". Accurate, but "service" is a Windows word, and macOS and the
    docs would still need "the host" for the same thing.
- **N2. Machines, agents, the relay and the links between them.**
  - **A (decided): plain words, unchanged.** "Gate" never appears in a status or an error. Machine, device, agent, session, delegate, and
    "the Slipgates relay". The link between two of them is "connected" or "offline", as ui.md
    §2 and §8 already say. The slipgate idea frames the product's introduction (§1), and nothing
    in the UI.
  - B: gates in the UI: a machine is a gate, and a link is "open" or "closed". It is evocative,
    but "closed" sounds deliberate where "offline" says what happened. It also collides with
    "open a session". Plain-language alternative: A.
  - C: "Slipgate" as a proper noun for one machine's endpoint ("ULTRA's slipgate"), with the
    plain states kept. It costs a word people must learn, and doesn't explain anything a
    machine's name doesn't.
- **N3. The bundled fonts.** The monospace was built and named "Agent Bridge Mono". A custom
  sans to pair with it is being explored in #544.
  - **What the licence allows.** [OFL.txt](../../design/fonts/slipgates-mono/OFL.txt) is SIL
    OFL 1.1, and none of its copyright holders (Iosevka, the IoskeleyMono configuration, and
    this project) declares a Reserved Font Name. A modified build may therefore take any name,
    and the old name doesn't have to stay. Every copyright line must stay in OFL.txt, so the
    line "for the Agent Bridge Mono modifications" stays as history.
  - **A (decided): one family name per face, under the brand.** Rebuild the monospace as
    **Slipgates Mono**, files `SlipgatesMono-*.ttf` and `.woff2`, and reserve **Slipgates Sans**
    for #544's face, so both read as one family. Declare no Reserved Font Name, as upstream
    doesn't, and don't reserve "Slipgates" itself as a font name: forks of this AGPL project then ship the fonts without renaming them. The brand is
    protected by its name and mark, not by the font licence. For one release, the font stack
    keeps "Agent Bridge Mono" after "Slipgates Mono" (in `tokens.json`, so the generated CSS and
    XAML carry it), and only then is it dropped. The precache, the ms-appx URIs, the
    `AgentBridgeMono-*` file names and the 6 PWA specs and SelfTest that pin the name all change
    in that one regenerated build (#537, #538).
  - **Done in #557**, which renamed the family and its files and shipped Slipgates Sans in one
    build. The generated stacks name Slipgates Mono only.
  - B: the same names, and reserve "Slipgates" as a Reserved Font Name for this project's
    modifications. That protects the name, but a fork that changes a glyph must rename the
    font, and the font then carries a second name rule beside the product's trademark.
  - C: keep "Agent Bridge Mono" (and later name the sans to match it). That costs nothing now,
    but leaves the old name in every client's font menu and in the inspector.
- **N4. The Windows listing name.** **A (decided): "Slipgates"** in the app, the Start menu
  and the installer. "Slipgates for Windows" appears only in a listing or release note that
  names several platforms. B: "Slipgates for Windows" everywhere on Windows.
- **N5. The device-name suffix.** **A (decided): "{device} · Slipgates"** on every client
  ("iPhone · Slipgates", "ULTRA · Slipgates"). B: the bare device name, with the client shown
  by its own icon. The protocol vectors that hold "iPhone · Agent Bridge" are test data and
  change only in one deliberate commit.

### 3.3 Never renamed by the brand

These keep their names until #541, which migrates each one deliberately. Renaming any of
them breaks installs, peers or saved data:

- **Windows identity:**
  - the toast AUMID and activator CLSID;
  - the Velopack packId and channels;
  - the exe and binary names (`AgentBridge.exe`, `agent-bridge-host*`);
  - the HKCU Run value, the single-instance mutex and the logon task's name;
  - the named pipe and socket names;
  - the `app.manifest` identity.
- **Data and configuration:**
  - `%LOCALAPPDATA%\AgentBridge` and `~/.agent-bridge`;
  - the `AGENT_BRIDGE_*` environment variables;
  - the `agent-bridge-host` CLI command in help text;
  - the worktree marker.
- **Wire and storage:**
  - the crypto domain-separation labels and protocol vectors;
  - the PWA's IndexedDB and localStorage keys;
  - the service worker's cache prefix and message names;
  - the JS globals `window.agentBridge*`.
- **Integrations:**
  - Home Assistant entity IDs, MQTT topics and notify actions;
  - agent-ha-bridge's names.
- **Hosting:**
  - the GitHub Pages URL;
  - `relay.bad-duck.com` and the Azure resource names;
  - the repository slug;
  - the GitHub App's client id.
- **Apple:** bundle IDs `dev.agentbridge.*`, the Keychain service, UserDefaults keys,
  notification thread and category IDs, and queue names.
- **Code:** namespaces, projects and the solution; the design tokens' `bridge` role and the
  `Ab*` and `--ab-*` prefixes.

## 4. The mark

Board: [proposal-536-mark.png](proposals/536-slipgates-brand/proposal-536-mark.png).
Source: [`design/icons/mark.svg`](../../design/icons/mark.svg), the approved v7 drawing.

### 4.1 Construction

- **Grid:** 48 units. The live area is the central 40, so the margin is 4.
- **Two linked gates.** The S is two identical hooks. The second is the first turned 180°
  about the centre, so each gate faces the other across the link. They never touch: the gap
  between them is the passage.
- **Bars:** 4.75 thick, with 0.9 corners. The bars' ends are cut at 18° from vertical, so the
  S leans forward.
- **Colour:** one colour, `currentColor`. As the brand, it is the Bridge role (§8).

### 4.2 Clear space and sizes

- **Clear space:** a quarter of the mark's height on every side. Nothing else (text, an edge,
  another icon) enters it. The app-icon plates (§6) are drawn with at least this space.
- **Minimum size:** 16 px. Below that, use the name, not the mark.
- **Native cuts.** At 24 px and under, the 48-unit drawing's bars would fall between pixels.
  Each small size has its own drawing on a pixel grid, so every bar lands on whole pixels:
  - 16 px: [`mark-16.svg`](../../design/icons/mark-16.svg);
  - 20 px: [`mark-20.svg`](../../design/icons/mark-20.svg), the size a session row draws (§5);
  - 24 px: [`mark-24.svg`](../../design/icons/mark-24.svg).

  Use the cut whose size you draw at, never one scaled from another size. From 30 px up, draw
  `mark.svg`.
- **The badge seat** (#500): a 5.6-unit circle at (40.5, 40.5), on the 48 grid. When a badge
  sits there, the mark is cut away 2.4 units around it. It is a real cut, not a disc painted in
  the background colour, so it holds on any surface.

## 5. Status glyphs: the S in every state

Board: [proposal-536-glyphs.png](proposals/536-slipgates-brand/proposal-536-glyphs.png),
which shows 16, 20 and 24 px in dark, light and high contrast, with a close-up at 56 px.

The Slipgates status glyphs draw a **session's** state on the mark itself. They are the same
states with the same meanings as [ui.md §2.1](ui.md). Only the drawing changes. ui.md's §2.2
words, its rules and what each state means all stand.

The S family draws a session's state wherever ui.md draws one: session rows, the session's
header, tray and menu-bar lists, and Needs you entries. A delegate is drawn inside a session,
so it keeps ui.md's ring glyphs. That way a session and a delegate never read as the same
thing (decided G1, §11).

| State | Glyph (`design/glyphs/`) | Drawing | ui.md §2.1 glyph it stands for |
|---|---|---|---|
| Needs you | `mark-needs.svg` | The S knocked out of a filled Needs tile, ink `needs.ink*` | `needs` |
| Working | `mark-working.svg` | The upper gate lit in Active; the lower one is the track | `working` |
| Delegating | `mark-delegating.svg` | A small S with three orbit dots, all in Active, in every theme. The dots tell it from Working by shape, not only colour | `delegating` |
| Background commands only | `mark-background.svg` | A dotted S in Active, in every theme: 3.6:1 or better on dark rows and 4.2:1 or better on light ones | `delegating` (ui.md §2.2, row 5) |
| Idle | `mark-idle.svg` | The S in Neutral | `idle` |
| Finished | `mark-finished.svg` (+ `mark-finished-badge.svg`) | The S with the check badge in its seat, all in Neutral | `done` (#500) |
| Failed | `mark-failed.svg` | The S in Danger | `failed` |
| Ended | `mark-ended.svg` | The S as a 1.5 px keyline at row sizes, in Neutral | `ended` |
| Finished, then ended | `mark-ended-finished.svg` | The ended keyline, still cut around the outlined badge | `ended` (#500) |
| Working, driven | `mark-driven.svg` | Working in Agent violet, with the driven diamond (`mark-driven-badge.svg`) in the seat | `working` + `driven-badge` |
| Finished, driven | `mark-finished.svg` with the badge in Agent violet | A violet check in place of the diamond | `done` + `driven-badge` (#500) |
| Connecting | `mark-connecting.svg` | The two gates drawn apart, in Neutral, while a machine's link comes up. It shares offline's shape; it differs by strength (full opacity), and by motion once #232 specifies it | (a machine connecting, ui.md §6.1, §6.6) |
| Offline | `mark-offline.svg` | The two gates standing apart, in Neutral, at **85% opacity** | `failed` (a host unreachable; a machine away, §6.1) |

The 16 and 24 px finished glyphs have their own cuts (`mark-finished-16*.svg`,
`mark-finished-24*.svg`). At 24 px the badge is a native check. At 16 px it is a 4 px dot,
violet when driven.

Approved rules:

1. **Rows draw the mark at 20 px.**
2. **Rule D, the unlit half on dark rows.** On a dark row, the track is the row colour tinted
   60% toward the lit colour (`active.trackRowDark` #4071AF and `agent.trackRowDark` #7768AC),
   so at 20 px the unlit gate still reads as part of the S. On light rows it is the role's
   track. High contrast draws it in `GrayText`. In the PWA these are `--ab-active-track-row`
   and `--ab-agent-track-row`; in XAML, `AbActiveTrackRow` and `AbAgentTrackRow`. The
   generated glyphs draw the track at 30% opacity. A row overrides that with the trackRow
   colour, as ui.md's `track*` rule allows: in the PWA it sets `--ab-glyph-track` to the role's
   `--ab-*-track-row` and `--ab-glyph-track-opacity` to `1`; in XAML it sets the glyph
   control's `BorderBrush` to `AbActiveTrackRow` or `AbAgentTrackRow`.
3. **The ended keyline is 1.5 px at row sizes.**
4. **Shapes, not only colours.** Background commands is a dotted S and delegating is a small
   S with three orbit dots, in every theme, so neither depends on colour to differ from
   working. The studies first drew them only in high contrast; the owner extended them to
   every theme on 2026-11-03 (G1, G3).
5. **Offline is the two gates standing apart, at 85% opacity.** That is the lightest value
   that keeps 3:1 against every row (3.16 to 3.43; 80% fell to 2.92). The glyph carries the
   85% itself, so clients add no opacity of their own; a second 85% would fall below 3:1.
   Connecting draws the same apart shape at full strength, so a still connecting glyph and an
   offline one differ only by that strength; the row's §2.2 word tells them apart.
6. **Finished is neutral at every size.** It has no green and no success token. Under
   deuteranopia a green dot collapses into failed, and on light rows it nearly matches the
   unread dot. The neutral badge already clears 3:1 (3.77 to 4.55).
7. **Driven is violet and nothing else is.** It is shown by the diamond while the session
   works, and by the check when it has finished. The row's text names who drove it, so colour
   is never the only signal (ui.md §3).
8. **Motion** stays as ui.md §5.3 and #500 give it: the finished badge springs in and its
   check draws on, and the ended fill drains. A looping motion for working and delegating on
   the S is not part of this spec. Until a #232 follow-up specifies one, clients draw the S
   family still, in every motion setting.

Each glyph is generated like every other: `pwa/design/sprite.svg#glyph-mark-<state>` and
XAML `AbGlyphMark<State>`.

## 6. App icon

Board: [proposal-536-app-icons.png](proposals/536-slipgates-brand/proposal-536-app-icons.png),
which shows every platform at every size it ships.

The app icon is the mark on a plate, in each platform's own container. `tokens.json`
`appIcon` holds every size, and `tools/design-gen` draws every file from it
([design/README.md](../../design/README.md#app-icon)). ui.md §7 gives the per-platform
geometry.

Plate rules:

- **One icon in every theme.** The plate is ink `#0E1114` with a `#2A2F36` hairline edge,
  and the mark on it is white (`appIcon.plate`, `edge` and `mark`; decided P, §11). The icon
  carries no status or accent colour, so an S on a row never reads as "the app".
- **The mark sits optically centred** in the plate's live area, at the size the container
  gives. Never add a second element, a gradient, a gloss or a drop shadow, except the shadow
  a platform's own template adds (macOS).
- **Small sizes draw a native cut** (§4.2): the Windows tile uses `mark-16`, `mark-20` and
  `mark-24` at 16, 17–20 and 21–24 px, with no edge.
- **Monochrome and template versions** are the mark alone: white on transparency, or black
  for an Apple template image. They are never the plate.

| Platform | Container | Generated files |
|---|---|---|
| Windows | Tile on a 72 grid: radius 14, a 1-unit edge `#2A2F36`, the mark 44 across. Native-cut tiles at 16, 20 and 24 | `design/app-icon/windows*.svg`; `src/AgentBridge.App/Assets/AgentBridge.ico` (16 to 256) and `AgentBridge.png` |
| PWA | The Windows tile as favicon and `any` icons; maskable icons full bleed, mark 35 on 72 | `pwa/icon.svg`, `pwa/icons/*.png` |
| macOS | Apple's 1024 grid: plate 824, radius 185, mark 504; the shadow is the system's | `design/apple/Assets.xcassets/AppIcon.appiconset/mac-*.png` (16 to 512 pt at 1x and 2x) |
| macOS menu bar | **Template image:** the native 16 px cut in black on transparency, at 1x and 2x | `design/apple/Assets.xcassets/MenuBarIcon.imageset` |
| iOS and iPadOS | Full bleed and opaque, mark 44 on 72; the system masks it. **Default, dark and tinted** appearances: dark and tinted are the mark alone on transparency, so the system draws the dark ground or tints the white mark | `AppIcon.appiconset/ios-1024.png`, `ios-1024-dark.png`, `ios-1024-tinted.png`; the PWA's `apple-touch-icon.png` |
| Android | Adaptive icon on the 108 dp grid: background (the plate), foreground (the mark 40 across) and **monochrome** (the mark, for Android 13+ themed icons) | `design/app-icon/android-*.svg`; `design/android/mipmap-anydpi-v26/ic_launcher.xml`, `drawable/ic_launcher_foreground.xml`, `ic_launcher_monochrome.xml`, `values/ic_launcher_background.xml` |
| Android status bar | **Notification icon:** the native 24 px cut, white on transparency, on the 24 dp grid | `design/android/drawable/ic_stat_slipgates.xml` |

## 7. Wordmark and lockup

Board: [proposal-536-lockup.png](proposals/536-slipgates-brand/proposal-536-lockup.png).

- **The wordmark** is "Slipgates" set in **Slipgates Sans** (#544) once that face ships, and
  in **Inter Tight SemiBold** until then (decided T, §11). Boards set it in live text, and no
  outlined wordmark file ships yet. When Slipgates Sans lands, an outlined
  `design/icons/wordmark.svg` is added, so no client depends on the face being installed.
- **The lockup** is the mark, then the wordmark:
  - the mark's box is the lockup's height;
  - the name's cap height is half of it, centred on the mark;
  - the gap between them is 0.3 of the mark's width;
  - the smallest lockup uses the 20 px mark cut.
- **Colour:** the mark takes Bridge, and the name takes the text colour (`text` on the
  surface). Neither ever takes a status colour. On a photo or a plate, both may take white.
- **In app chrome** (a title bar, the PWA's app bar, an About page), write the name in the
  platform's UI face beside the mark, not in the wordmark. The wordmark is for the
  README, store listings, social images and the installer's first screen.
- **No descriptor.** The name stands alone in the lockup (decided D, §11). A store or install
  listing keeps its plain description as copy, not as part of the logo.
- **Only real font faces.** Every weight and style is a face the font actually has: Inter
  Tight SemiBold, Slipgates Mono SemiBold, Segoe UI Variable Semibold. Never synthesise one:
  no faux bold (`font-weight` with no matching face, a stroke added to fill), no faux italic
  or oblique (`font-style: oblique` or a skew transform on an upright face), and no
  stretched or condensed text. In CSS, every `@font-face` lists the weights it ships, and
  `font-synthesis: none` is set wherever brand type is drawn. In XAML and SwiftUI, set only the
  weights the bundled files provide.
- **Never:**
  - redraw, outline, rotate, flip or recolour the mark per letter;
  - put the mark inside the name, or use it as the S in "Slipgates";
  - write the name in all caps or letter-space it, except where a platform does so in its
    own chrome.

## 8. Colour roles

The brand adds **no brand colour.** It is drawn with the roles in `tokens.json`:

- **Bridge** (`#2DD4BF` dark, `#0B7A6E` light, `Highlight` in high contrast) is the mark's
  colour and the brand's accent. It is the same role that colours primary actions and focus,
  so the product reads as one thing. The role keeps its token name, `bridge`, until #541.
- **Text and surfaces** carry the name. The app icon's plate is `appIcon.plate`, ink
  `#0E1114`, with a white mark.
- **Status roles** (Needs, Active, Agent, Danger, Neutral) colour the status glyphs (§5),
  never the mark as a logo or the name.

**New tokens, and why:**

- `brand` (§2): the name and support email, so one source feeds every client.
- `active.trackRowDark/Light` and `agent.trackRowDark/Light`, with high contrast `GrayText`.
  Rule D needs a **solid** tint on rows: the row colour mixed 60% toward the lit colour. A
  30%-opacity track can't express that. XAML has one opacity per path, a `<use>` in the
  PWA can't be restyled per part, and an opacity over a row's hover and selection colours
  would shift with them. Light rows reuse the existing track colours, so they add no
  colour.
- `appIcon.container` gained `windows20`, `windows24`, `glyph16` (the menu-bar template)
  and `glyph24` (the Android notification icon), and the `markFile` field. `appIcon.plate`,
  `edge` and `mark` changed to ink `#0E1114`, `#2A2F36` and white `#FFFFFF` (decided P).
  `mark` may now be a colour as well as a role's name, because white is no role.

## 9. Do's and don'ts

**Do:**
- Draw the mark from `design/icons`, using the cut for the size you draw at.
- Take the name from the brand token. Never retype it.
- Use plain words in UI copy, and save the slipgate idea for introductions.
- Keep the clear space, and keep the plate's colours from the tokens.
- Show a state with the §5 glyph and its §2.2 word together.
- Check every new surface in dark, light and high contrast, and at 100% and 200% scale.

**Don't:**
- Trace, re-export or hand-edit a generated icon or glyph. Change the SVG or the token, then
  run the generator.
- Put a status colour on the brand mark, or a brand colour on a status.
- Show finished in green, or add a success token.
- Use the display mark below 30 px, or scale one native cut to another size.
- Add flavour text: "Opening the gate…", "Gate closed", "Slipping through…".
- Show `app.slipgates.com` or `relay.slipgates.com` before #393 and #540 make them live.
- Rename an identifier in §3.3 as part of a brand change.
- Synthesise a bold, an italic or an oblique (§7).

## 10. Acceptance per surface

Each surface's issue is done when its list is checked in dark, light and high contrast. The
before-and-after boards show the target:
[Windows](proposals/536-slipgates-brand/proposal-536-windows.png) and
[PWA](proposals/536-slipgates-brand/proposal-536-pwa.png).

### Windows (#537)
- [ ] Every user-visible "Agent Bridge" reads `DesignBrand.Name` or `AbBrandName`: the window
  title and title bar, the tray tooltip and menu, the toast `DisplayName`, the update toast,
  `ToastResponder`'s default title, the device name and the host's messages. SelfTest and
  tests assert the constant, not a literal.
- [ ] `Product` and `AssemblyTitle` are "Slipgates", so Task Manager, Start and the file's
  properties show the name.
- [ ] The installer's `packTitle` and release titles come from one `$ProductName`.
- [ ] "Bridge Host" strings follow N1. The Settings section follows N1.
- [ ] The regenerated `AgentBridge.ico` and `.png` are checked on the taskbar, tray, Start,
  Alt+Tab, the installer and toasts, at 100%, 150% and 200% scale, on a light and a dark
  taskbar. The 16, 20 and 24 px tiles are crisp.
- [ ] Session rows draw `AbGlyphMark*` at 20 px, with `AbActiveTrackRow` and
  `AbAgentTrackRow` for the unlit half, no extra opacity on offline (the glyph carries its 85%),
  and the dotted background and orbit-dot
  delegating shapes, in every theme.
  Finished is neutral. Each state is exposed to assistive technology as its §2.2 word.
- [ ] The title bar's mark uses `AbIconMark20`, the native 20 px cut, not `AbIconMark`.
- [ ] Nothing in §3.3 changed.

### PWA (#538)
- [ ] `<title>`, `apple-mobile-web-app-title`, the manifest's `name` and `short_name`,
  `aria-label`s, `.brand-name`, the sign-in, empty and notification copy, the service
  worker's fallback title and the device name all use `BRAND` from `pwa/design/brand.js`.
  The manifest is written from it or checked against it.
- [ ] The regenerated favicon, `any`, maskable and `apple-touch-icon` icons are checked when
  installed on Android (masked by the launcher), iOS and iPadOS, Windows and macOS.
- [ ] Rows draw `glyph-mark-*` at 20 px with `--ab-active-track-row` and
  `--ab-agent-track-row`, no extra opacity on offline (the glyph carries its 85%), and the
  dotted background and orbit-dot delegating shapes in
  every theme, `forced-colors` included.
- [ ] The app bar shows the mark with the name in `system-ui` (§7), not the wordmark.
- [ ] The app bar's mark uses `icon-mark-20`, the native 20 px cut, not `icon-mark`.
- [ ] Storage keys, cache names and the Pages URL are unchanged (§3.3). `app.slipgates.com`
  is not shown until #393.

### Docs (#539)
- [ ] The README's title, introduction (one or two sentences from §1) and screenshots say
  Slipgates, with "(formerly Agent Bridge)" until #541.
- [ ] The GitHub App's display name is "Slipgates (dev)".
- [ ] Release notes and the docs' product name follow §3.1. Code identifiers, commands and
  paths in the docs keep their real names (§3.3).
- [ ] The repository's social preview uses the lockup (§7): Inter Tight SemiBold until Slipgates Sans
  (#544) ships, with no descriptor.
- [ ] No unlaunched address appears.

### macOS (#260)
- [ ] The app takes `design/apple/Assets.xcassets` whole: the AppIcon (16 to 1024) and the
  `MenuBarIcon` template image. The menu-bar extra draws `MenuBarIcon` as a template, replacing
  the SF Symbol `terminal`, with the waiting count beside it as today.
- [ ] `CFBundleDisplayName` is "Slipgates". The menu reads Open Slipgates, Quit Slipgates and
  About Slipgates. Notification fallback titles, login-item copy and "Connect to Slipgates"
  read `Brand.name`.
- [ ] The device name follows N5.
- [ ] Session rows follow §5 once SwiftUI shapes are generated (§12).

### iOS and iPadOS (#260)
- [ ] The AppIcon's default, dark and tinted appearances (`ios-1024*.png`) are checked on
  the Home Screen in each appearance, and in Settings and Spotlight.
- [ ] The Home Screen label is `Brand.shortName`.
- [ ] Widgets and Live Activities follow the W decision (§11).
- [ ] Notifications show the app icon (the system's), and their fallback title is
  `Brand.name`.

### Android (the Android client, P8; today, the PWA installed on Android)
- [ ] The installed PWA uses the maskable icons (above).
- [ ] The native client takes `design/android` whole: the adaptive icon with its monochrome
  layer (checked with themed icons on, Android 13+), `ic_stat_slipgates` as every
  notification's small icon, and `@string/brand_name` for the app label.
- [ ] The home-screen widget and notification follow decision W (§11): status first, a small
  mark header, and `ic_stat_slipgates` as the small icon.

## 11. Decisions

The owner decided every open item on 2026-11-03 and chose the recommended option each time,
with two refinements to the glyphs (G1, G3). Each board keeps the options that were weighed,
with the chosen one marked.

| | Decision | Chosen, and what was weighed | Board |
|---|---|---|---|
| T | **Wordmark typeface** | **A: Slipgates Sans (#544) once it ships, set in Inter Tight SemiBold until then.** Nothing waits for #544. Weighed: Slipgates Mono SemiBold; each platform's UI face | [decision-typeface](proposals/536-slipgates-brand/proposal-536-decision-typeface.png) |
| P | **App-icon plate colour** | **A: ink `#0E1114` with a `#2A2F36` edge and a white mark.** Now in `tokens.json` and every generated icon. Weighed: the earlier deep teal `#0F1F22` with a Bridge mark; a Bridge plate with an ink mark | [decision-plate](proposals/536-slipgates-brand/proposal-536-decision-plate.png) |
| W | **Widgets, Live Activity, Android widget and notification icon** | **A: status first.** A small mark heads a widget. The Dynamic Island and compact Live Activity show only state glyphs and counts. The Android widget follows the iOS widget, and the Android notification's small icon is the mark (`ic_stat_slipgates`). Weighed: brand forward; no brand inside | [decision-widgets](proposals/536-slipgates-brand/proposal-536-decision-widgets.png) |
| D | **Descriptor line** | **A: none.** The name stands alone. Weighed: "Agent sessions, anywhere"; "Instant access to your agents" | [decision-descriptor](proposals/536-slipgates-brand/proposal-536-decision-descriptor.png) |
| N1–N5 | **Naming** | **A in each** (§3.2): "the Slipgates host", then "the host"; plain words, with "gate" never in status or error copy; Slipgates Mono with Slipgates Sans reserved and the old name as a one-release fallback, and no Reserved Font Name; "Slipgates" in the app; "{device} · Slipgates" | [decision-naming](proposals/536-slipgates-brand/proposal-536-decision-naming.png) |
| G1 | **Where the S family draws** | **A session's state everywhere it is drawn** (rows, the header, tray and menu-bar lists, Needs you), while delegates keep the ring glyphs. **Refinement:** delegating keeps its three orbit dots in every theme, not only in high contrast, so it differs from working by shape | [glyphs](proposals/536-slipgates-brand/proposal-536-glyphs.png) |
| G2 | **Offline glyph** | **A: the S halves pulled apart, at 85% (§5).** Weighed: keeping the failed ring for a machine that is away | [glyphs](proposals/536-slipgates-brand/proposal-536-glyphs.png) |
| G3 | **Background-commands glyph** | **The dotted S in every theme, at 3:1 or better on light and dark rows.** It replaces the track-only S, which fell to about 1.35:1 on light rows. Drawn in Active, it measures 3.60 to 5.54:1 on dark rows and 4.19 to 5.34:1 on light ones, selected rows included | [glyphs](proposals/536-slipgates-brand/proposal-536-glyphs.png) |

## 12. Known limits of this PR

- **Nothing reads the new outputs yet.** The S glyph family, the brand token and the
  Apple and Android files are generated and checked, but no client uses them until #537,
  #538, #260 and the Android client adopt them. Two things are visibly different
  today. The regenerated Windows `.ico` and `.png` and the PWA's icons now draw the v7 mark.
  And `icon-mark` / `AbIconMark` (from `design/icons/mark.svg`), which the PWA app bar and
  the Windows title bar already draw, now show the S scaled down from the 48 grid; #537 and
  #538 switch them to the native 20 px cut.
- **macOS 16 and 32 px** draw the display mark scaled. A native cut for the Apple 16 pt grid
  is a follow-up, if the 16 px Finder icon looks soft on a non-Retina display.
- **No SwiftUI shapes or Compose `ImageVector`s** are generated for the status glyphs yet, only
  the app icons and the brand constants. They come with the Apple (P5) and Android (P8) design
  tokens.
- **The wordmark** ships as no file until Slipgates Sans (#544) lands (§7).
