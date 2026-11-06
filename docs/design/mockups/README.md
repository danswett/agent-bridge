# Design mock-ups

Every board from the design canvas except the `Proposal-*` ones, first rendered on 2026-11-01.
The list boards, the Windows and PWA desktop boards, the design language and the status rules
were redrawn on 2026-11-02 for #253 (Needs you · N is a filter, and a waiting session is an
ordinary row). The design language and the Windows and PWA desktop boards, which show the
product's name, were re-rendered on 2026-11-05 for Slipgates (#539), and so also show Slipgates
Mono (#557). The proposal boards render apart, into
[proposals/253-attention](../proposals/253-attention). **These are reference, not spec:**
[ui.md](../ui.md) is the spec, and it wins wherever an image disagrees with it.

**Text fonts are stand-ins:** Inter takes the place of Segoe UI and SF Pro, so trust the
images for layout, colour, size and spacing, not letterforms. The monospace is the real
[Slipgates Mono](../../../design/fonts/slipgates-mono/README.md). To
regenerate them, see [tools/design-mockups](../../../tools/design-mockups); the source is
in [design/canvas](../../../design/canvas).

## Design language

![Design language](main.png)

## Windows (WinUI 3)

| Dark | Light |
|---|---|
| ![Windows, dark](windows.png) | ![Windows, light](windows-light.png) |

## macOS

| Light | Dark |
|---|---|
| ![macOS, light](mac.png) | ![macOS, dark](mac-dark.png) |

## PWA, desktop browser (follows Windows)

| Dark | Light |
|---|---|
| ![PWA desktop, dark](pwa-desktop.png) | ![PWA desktop, light](pwa-desktop-light.png) |

## iOS

| Sessions, dark | Sessions, light | Session and delegates, dark | Session and delegates, light |
|---|---|---|---|
| ![iOS sessions, dark](ios-list.png) | ![iOS sessions, light](ios-list-light.png) | ![iOS session, dark](ios-session.png) | ![iOS session, light](ios-session-light.png) |

## Android

| Sessions, light | Sessions, dark | Session waiting on you, light | Session waiting on you, dark |
|---|---|---|---|
| ![Android sessions, light](android-list.png) | ![Android sessions, dark](android-list-dark.png) | ![Android session, light](android-session.png) | ![Android session, dark](android-session-dark.png) |

## PWA, phone (follows Windows)

| Sessions, dark | Sessions, light | Session, dark | Session, light |
|---|---|---|---|
| ![PWA phone, dark](pwa-phone.png) | ![PWA phone, light](pwa-phone-light.png) | ![PWA phone session, dark](pwa-phone-session.png) | ![PWA phone session, light](pwa-phone-session-light.png) |

## Status rules

![Status rules](states.png)
