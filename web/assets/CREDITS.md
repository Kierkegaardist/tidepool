# Tidepool v4: credits

Every image here was made by a person. The rule was no AI-made or AI-assisted images:
- the lake, skyline and star photos are camera photos from Wikimedia Commons with no AI tag in their categories;
- the scenes are Blender camera views of Poly Haven HDRIs, which are real 360° photos;
- the icons are from Crystal Clear, an icon set drawn by Everaldo Coelho in 2002–2005;
- the Winamp skins are classic hand-made skins from the Winamp Skin Museum (authors and dates below).

The only thing I drew is the SVG flower on the Start orb, which the user's update allows. The glass, gloss, shadows, sun, moon, light rays, bubbles and fireflies are CSS effects.
All files were downloaded on 2026-11-12 and are stored under this folder.

## Scene photos (`img/`)

Lake photo: see "Carried over from v3" below. The other four scenes are Blender renders of Poly Haven HDRIs. An HDRI is a real 360° photo stitched from camera shots. I put a camera at the centre of each and rendered one 16:9 view (settings in `blender/README.md`). Nothing was painted or generated. Poly Haven's FAQ says it does not make assets with AI. All four are CC0, so no credit is required, but they are listed here.

| File | HDRI | Photographer | Shot | Licence |
|---|---|---|---|---|
| `img/hill.jpg` | [Spaichingen Hill](https://polyhaven.com/a/spaichingen_hill) | Andreas Mischok | 2021-09-04, Germany | CC0 |
| `img/meadow.jpg` | [Cloud Layers](https://polyhaven.com/a/cloud_layers) | Greg Zaal | 2015-12-29, South Africa | CC0 |
| `img/valley.jpg` | [Alps Field](https://polyhaven.com/a/alps_field) | Andreas Mischok | 2022-04-14, Switzerland | CC0 |
| `img/shore.jpg` | [Lakeside](https://polyhaven.com/a/lakeside) | Greg Zaal | 2019-05-01, South Africa | CC0 |

## Icons (`icons/`)

From the [Crystal Clear](https://commons.wikimedia.org/wiki/Crystal_Clear) icon set by Everaldo Coelho / YellowIcon (2002–2005), licensed LGPL. I checked the Commons categories and none has an AI tag. The originals are 128 px PNGs.

| File | Commons source | Change |
|---|---|---|
| `icons/music.png` | [Crystal Clear app xmms](https://commons.wikimedia.org/wiki/File:Crystal_Clear_app_xmms.png) | none |
| `icons/studio.png` | [Crystal Clear app kcmmidi](https://commons.wikimedia.org/wiki/File:Crystal_Clear_app_kcmmidi.png) | none |
| `icons/games.png` | [Crystal Clear app package games](https://commons.wikimedia.org/wiki/File:Crystal_Clear_app_package_games.png) | none |
| `icons/web.png` | [Crystal Clear app browser](https://commons.wikimedia.org/wiki/File:Crystal_Clear_app_browser.png) | none |
| `icons/folder.png` | [Crystal Clear filesystem folder blue](https://commons.wikimedia.org/wiki/File:Crystal_Clear_filesystem_folder_blue.png) | none |
| `icons/download.png` | [Crystal Clear app download manager](https://commons.wikimedia.org/wiki/File:Crystal_Clear_app_download_manager.png) | none |
| `icons/desktop.png` | [Crystal Clear app kfm home](https://commons.wikimedia.org/wiki/File:Crystal_Clear_app_kfm_home.png) | none |
| `icons/claude.png` | [Crystal Clear app xchat](https://commons.wikimedia.org/wiki/File:Crystal_Clear_app_xchat.png) | cropped to the speech bubble (the penguin is cut off) |
| `icons/volume.png` | [Crystal Clear app xmms](https://commons.wikimedia.org/wiki/File:Crystal_Clear_app_xmms.png) | cropped to the speaker |
| `icons/orb.svg` | none: a five-petal flower I drew in SVG for the Start orb | allowed under the user's 2026-11-12 update on code-made art |

LGPL lets this desktop use and ship the icons. The two cropped files are modified, so they stay LGPL; a shipped build should include the LGPL text and a link to the originals.

## Winamp skins (`skins/`)

Replaced 2026-11-13: the user found the old four (their own PS2, XBOX and Silent Hill skins) dissonant with Tidepool. These come from the
[Winamp Skin Museum](https://skins.webamp.org/), picked for the aqua glass look and installed by `app/tools/skin-hunt/install.ps1`
(bitmaps converted BMP -> PNG unchanged). Each was checked for a human author and real archive dates; "Frutiger-Aero.wsz" was rejected
because its readme says a script drew its bitmaps and its file dates are faked, and "FrutigerGlassv1" because one file was changed in 2025.

| Folder | Skin | Author, date | Terms / notes |
|---|---|---|---|
| `skins/aero/` | Frutiger Aero | SpaceKitty (winamp.spacecatsamba.com), June 2023 | "may be freely redistributed so long as all files remain" (readme kept) |
| `skins/aquadeck/` | Aquadeck | samiximas (DeviantArt), 2008-2009 | no readme |
| `skins/aquila/` | Raydream Aquila Blue | kaotix (naoneo.com), October 2001 | readme kept |
| `skins/drops/` | waterdrops | unknown, files 1998-2006 | no readme |
| `skins/aquax/` | Aqua X | Freddy Leitner, 2000 | readme kept |
| `skins/vista/` | Winamp Vista Classic | Gince80, 2008 | readme kept |
| `skins/pearl/` | Blue Pearl | unknown, April 2001 | no readme |

## Carried over from v3

| File | Source | Author | Licence |
|---|---|---|---|
| `img/hero.jpg` | [Split Level Near Apgar Boat Dock](https://commons.wikimedia.org/wiki/File:Split_Level_Near_Apgar_Boat_Dock_(20909087258).jpg), 2015 | Glacier National Park Service | Public domain (US NPS) |
| `img/skyline.png` | [Skyline Frankfurt am Main 2015](https://commons.wikimedia.org/wiki/File:Skyline_Frankfurt_am_Main_2015.jpg) | Christian Wolf (www.c-w-design.de) | CC BY-SA 3.0 DE. The sky is keyed out, so this derived file is also CC BY-SA 3.0 DE. |
| `img/stars.jpg` | [The Milky Way in Crater Lake](https://commons.wikimedia.org/wiki/File:The_Milky_Way_in_Crater_Lake_(26972610245).jpg), 2015 | NPS Natural Resources | Public domain (US NPS) |

The goldfish photo from v3 is gone, because the fishbowl was removed.
