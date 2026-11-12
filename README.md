# AXAML Previewer

A live, interactive previewer for the `.axaml` views of [Avalonia®](https://avaloniaui.net) apps. It sits next to your
editor and follows it. Pairs with [axaml-ls](https://github.com/Engel-167/axaml-ls) and the
[AXAML extension for Zed](https://github.com/Engel-167/zed-axaml).

- **Open** a `.slnx`, `.sln` or `.csproj` (or start following your editor from the Home screen). The desktop app project, the one that references `Avalonia.Desktop`, runs the preview. Views in other projects (a shared `App` library, for example) are rendered with their own assembly.
- **Live**: saving the `.axaml` file re-renders it. C# changes need a rebuild: the ⟳ button rebuilds the project and restarts the preview.
- **Interactive**: hover, click, scroll and type into the design.
- **Canvas**: Ctrl + wheel zooms around the pointer; Ctrl + 0 resets it. Dragging the empty canvas (or using the middle button anywhere) pans. The wheel scrolls the design when the pointer is over it, and the canvas otherwise. The zoom menu has *Fit*.
- **Theme**: preview with no theme override, Light or Dark. The variant is applied through a design-only style, so your file is never changed.
- **Follow the editor** (the round button, or *Connect to IDE*): the preview shows the `.axaml` file you are on. Switching to any other kind of file keeps the last preview. If the file belongs to a different project, that project is opened.

## Install

```sh
dotnet tool install -g axaml-previewer
axaml-previewer [project.slnx | view.axaml]
```

From source: `mise run install` packs it and installs the global tool from the local package.

It runs on the .NET SDK you already have (10 or later). The preview itself runs on your app's own runtime and Avalonia version, through the designer host that ships in the `Avalonia` package.

## Editors

| Editor | How the preview follows it |
|---|---|
| Zed | Reads Zed's workspace database (`~/.local/share/zed/db/0-*/db.sqlite`), which Zed updates about half a second after a tab switch. Read-only; the schema is Zed's own and may change. |

Other editors can be added by implementing `IEditorFollower`.

## Development

`mise run run -- [project.slnx | view.axaml]` runs it from source.


`AXAML_PREVIEW_SNAPSHOT=/tmp/shot.png` saves a PNG of the window every 3 s (`shot-1.png`, `shot-2.png`, …). Useful for checking the UI without a screenshot tool.

The design is in Figma: Home Screen and Open File Screen (Material 3 dark).

## License

MIT, see [LICENSE](LICENSE). Bundled fonts and icons are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Avalonia is a registered trademark of AvaloniaUI OÜ. This project is not affiliated with or endorsed by AvaloniaUI OÜ.
All other trademarks are the property of their respective owners.
