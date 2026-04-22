# MD Translator Viewer

Dedicated Windows Markdown document viewer with built-in document translation.

## What It Does

- opens local `.md`, `.markdown`, `.mdx`, and `.mkd` files
- keeps multiple Markdown documents open with tabs
- renders Markdown inside a native document reader window
- can translate the current document on demand
- keeps code fences and inline code untouched
- watches the current file and reloads on save
- opens linked Markdown files inside the same app
- remembers whether translation was enabled the last time you used it

## Run From Source

```powershell
dotnet run --project .\MdTranslatorViewer.csproj -- D:\path\to\document.md
```

Without a file argument, launch it and use `Open File`.

## Publish

```powershell
dotnet publish .\MdTranslatorViewer.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o .\app
```

The synced executable will be under `.\app\`
using the same slim multi-file layout as the default release ZIP.

Create a release ZIP for distribution:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\create-release-zip.ps1 -Version v0.1.0
```

Replace `v0.1.0` with the tag you are publishing.

This default release is framework-dependent to keep the ZIP smaller and the folder layout cleaner.
It requires the .NET 9 Desktop Runtime on the target machine.

If you need a runtime-included package instead, build the larger self-contained variant explicitly:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\create-release-zip.ps1 -Version v0.1.0 -DeploymentMode self-contained
```

The packaged output will be under `.\dist\`
along with a `.sha256.txt` checksum file that can be uploaded to GitHub Releases.

Published outputs ship as multi-file folders instead of a single bundled EXE.
Their tabs, settings, translation cache, diagnostics log, and WebView2 session data are stored under `.\data\` beside the executable when the app is launched from a writable folder.

## GitHub Release

1. Publish or sync the repository to GitHub.
2. Create a new release from the GitHub Releases page.
3. Create a version tag such as `v0.1.0`.
4. Run `powershell -ExecutionPolicy Bypass -File .\scripts\create-release-zip.ps1 -Version v0.1.0`
5. Upload `.\dist\MdTranslatorViewer-v0.1.0-win-x64-framework-dependent.zip`
6. Upload `.\dist\MdTranslatorViewer-v0.1.0-win-x64-framework-dependent.zip.sha256.txt`

For portable behavior, tell users to extract the ZIP to a normal writable folder such as `Downloads` or `Documents`, not `Program Files`.

## File Associations

Register the viewer as an available Markdown app without taking over the Windows default app:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\register-file-associations.ps1
```

Only use `-SetAsDefault` if you explicitly want this viewer to become the per-user default handler for Markdown files:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\register-file-associations.ps1 -SetAsDefault
```

## Notes

- Rendering uses WPF + WebView2 inside the app, not a separate Edge window.
- Translation currently uses Google's public translation endpoint from inside the app.
- Relative Markdown links are opened inside the same viewer window.
- Other links are opened with the system default browser.
- Raw HTML inside Markdown is treated as plain text for safety.
