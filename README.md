# GTPMB

Extracts and builds Gran Turismo 3's menu files: `.pmb` (menu pages + their textures) and
`.mbl` (the menu list: page codes, PMB file numbers and the path through the menus).

## What the files are

**PMB** - a container of menu *pages* plus the texture files they use:

* pages hold layers and elements (images, texts, colored quads, composites) with
  positions, colors, text + font references, and texture references **by file index**;

**MBL** - one entry per menu page: the page's name, which numbered PMB holds it, its
menu page code, its parent (the "back" path), and the page's interactive items
(element on the page, target page, extra bindings).

## Workflow

The top-level workflow is the **menu source tree** - one menu family (an `.mbl` plus all its
numbered PMBs) as one editable folder, the source > compile model:

```
<family>/
  menu.txt            the menu list: pages, flags, returns, items
  <index>/            one folder per <family><index>.pmb
    pages.txt         the page structures, decompiled
    image_*.png         the textures
    pmb_config.ini    build inputs (file order, gzip metadata)
```

* **Extract menu source tree** takes one `.mbl` plus the folder holding its PMBs (a
  language folder for the shipped game) and produces the family folder.
* **Build menu source tree** compiles the folder back into `<family>.mbl` and every
  `<family><N>.pmb`. An optional tick runs "Update file entries" over every PMB folder
  first, so added / removed / renamed textures are picked up in one go.
* **Check menu source tree** validates the connections: every menu page must exist in
  its PMB, every item must name an element on its page; functions and returns that
  leave the family (another family's page, or a game action like `buy_car`) and pages
  only game code reaches are listed for review. The shipped `gt_mode` family
  (397 pages, 54 PMBs) checks out with zero findings.

`menu.txt` uses the same syntax as `pages.txt`; empty labels and default values are
omitted, so a page reads like what it is:

```
MenuPage {
    name string {"root"}
    pmb digit {1}
    flags digit {0x163}
    items [9] {
        Item {
            element string {"start"}
            layer2 string {"layer1"}
            function string {"qm_start"}
            flags digit {0x2}
        }
        ...
    }
}
```

An item's `function` is another page's name or a game-code action; an item can instead
jump with `return` + `pmb`.

The individual pieces underneath:

* **Extract PMB to folder** writes every texture as an editable `NNN_<name>.png`
  (named after the gzip file name, `NNN` = the file index the pages reference),
  the page structures **decompiled to `pages.txt`**, and `pmb_config.ini`.
  Anything that is not a single-texture Tex1 is dumped as a raw file and packed back as-is.
* **`pages.txt`** is the editable source of the menu pages, styled after GT4's
  `.mproject` files (the later generation of the same UI system)

  ```
  TextFace {
      name string {"message"}
      flags digit {284}
      geometry rectangle {0 236 640 32}
      text_color RGBA {204 204 204 127}
      text string {"Purchased"}
      font string {"sw_16#1"}
  }
  ```

  Elements are `Face` / `ImageFace` / `ColorFace` / `TextFace` / `Composite` nodes with
  geometry, corner colours, text + font, a `key` (runtime data binding like
  `render_model` or `rank_disp`) and `image` references by extracted file name.
  The compiler derives the binary type from the properties present; recompiling an
  unedited file is byte-identical for every current-format PMB in the game.
* **Generate PMB from .ini** rebuilds the PMB: `pages.txt` is compiled (image names
  resolve against the `[Files]` list), PNGs become Tex1 (format picked from the
  distinct colour count: up to 16 -> PSMT4, up to 256 -> PSMT8, more -> PSMCT32 - never
  quantized, and entries are re-gzipped with their stored
  names and timestamps.
* **Update PMB file entries** regenerates the config after files were added, removed or
  renamed: entries are ordered by file name (that order is what pages reference), and a
  checkbox list sets each file's compression individually - files known to the config
  keep their setting, new files start unticked.
* **Extract MBL to text** / **Generate MBL from text** - one MBL on its own, in the
  `menu.txt` format above. The rebuild is byte-identical for every shipped MBL, so page
  flags, menu file numbers and the menu path can be edited directly.
* **Inspect PMB / MBL structure** shows what is inside: the page tree with every
  element (type, position, size, texture link, text, font), the embedded file list,
  or the MBL's page graph.
* **Inspect color depths** reports the distinct colour count of every PNG in a folder,
  grouped by the palette size it fits (16 / 256 / true colour) - what decides the
  rebuilt Tex1 format.

## Building

Requires the .NET 10 SDK (Windows).

```
dotnet build GTPMB.slnx -c Release
dotnet publish src\GTPMB\GTPMB.csproj -c Release -p:PublishProfile=win-x64-selfcontained   # one exe, needs nothing installed
dotnet publish src\GTPMB\GTPMB.csproj -c Release -p:PublishProfile=win-x64-framework       # small exe, needs the .NET 10 Desktop Runtime
```

## Layout

```
src/GTPMB.Core/          the library (net10.0, no UI)
  Foundation/            RgbaImage, ByteReader, ByteWriter        (from GTGPBc)
  Imaging/               Png                                      (from GTGPBc)
  Compression/           gzip member reader / writer, CRC-32
  Textures/Tex1/         PS2 texture sets (GS memory emulation)   (from GTTexEdit, + PSMCT24 transfers)
  Config/                IniFile                                  (from GTGPBc)
  Pmb/                   PmbFile (container), PmbExtractor, PmbBuilder, PmbConfigGenerator,
                         MenuSource (source tree extract / build / check),
                         PageTree + PmbInspector (read-only decode), ColorInspector
  Pmb/Pages/             PageModel (the binary page codec) + PageText (pages.txt decompiler/compiler)
  Mbl/                   MblFile (full codec), MenuText (menu.txt decompiler/compiler)
src/GTPMB/               the WinForms app
```

## Technical

* Rebuilt gzip entries carry fresh deflate streams: PDI's 1999-era zlib output cannot be
  reproduced byte for byte, and the game only inflates them. File names, timestamps and
  the OS byte in the gzip headers are preserved through the config. (An untouched archive
  still rewrites byte-identical: unedited entries splice their original bytes.)
* In `pages.txt`, the element node names (`TextFace`, `Composite`, ...) are for readability
 - the binary element type is derived from which properties are present, which
  matches the entire shipped game with zero exceptions. `flags`, `sub`, the `z` value
  and the two `text_code` words are preserved as numbers; their meanings are still open.
