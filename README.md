# Byterake

An offline disk cleaner for Windows 10 and 11. It shows what is using space on a drive, explains which items are safe to clean, and cleans only what you confirm.

- **No network.** No AI, no API keys, no telemetry, no update checks, no crash reporting. Every rule is in a local file.
- **Recycle Bin by default.** Files go to the Recycle Bin so you can restore them. Permanent delete is a separate option that asks twice.
- **A hard-coded never-touch list** protects Windows, your programs and your personal folders. It cannot be switched off.
- **Never runs as admin unless you ask.** It asks only when you tick an item that needs admin rights, and says so first.

## Download

Get **`Byterake_Setup_v1.0.0.exe`** from the [latest release](https://github.com/sarathlal-12/byterake/releases/latest) and run it. Setup installs to `Program Files` and asks for admin rights once. Byterake itself then starts with normal rights. It needs Windows 10 or 11 (64-bit), and nothing else needs to be installed.

Byterake isn't code-signed, so Windows SmartScreen may say "Windows protected your PC". Choose **More info → Run anyway**.

To uninstall, use Settings > Apps, or **Uninstall Byterake** in the Start menu. Your activity logs in `%LOCALAPPDATA%\Byterake\Logs` are kept.

See [KNOWN-LIMITATIONS.md](KNOWN-LIMITATIONS.md) for what is untested and what to expect.

## What it does

| Page | What you get |
|---|---|
| **Overview** | Total, used and free space for the chosen drive. A Refresh button. The cleanup catalog with sizes, risk labels and admin flags. A ring that tracks your free-space goal (100 GB by default; you can edit it). |
| **Scan folders** | Walks a whole drive or a folder you choose and shows a sorted folder tree plus the largest files. Shows progress and can be cancelled. Links and junctions are skipped. Folders it cannot open are logged and skipped. |
| **Review files** | Old installers in Downloads (.exe, .msi and .zip over 50 MB), and large files over a size you set (500 MB by default) from the last drive scan. Nothing here is ticked for you. Protected files show greyed out with a lock. |
| **Uninstall apps** | Installed programs from the registry, with size and install date. **Uninstall…** runs the program's own uninstaller after you confirm. Byterake never deletes program folders. |
| **Activity log** | Every scan, delete, skip and refusal, with the reason. Saved as plain text in `%LOCALAPPDATA%\Byterake\Logs`. **Open log folder** opens it. |

### Cleaning, step by step

1. Tick items in the catalog. **Safe** items start ticked; **Review** items never do.
2. Press **Clean selected…**. If an item needs admin rights, Byterake tells you which ones. You can then restart as administrator (your ticks are kept), clean the others only, or cancel.
3. The confirmation screen shows the exact number of items, their total size, and free space now and right after. You choose Recycle Bin or permanent delete. Permanent delete asks again.
4. In Recycle Bin mode, a folder whose contents are exactly the confirmed files (checked again right before the move: nothing extra, no links, no system files) is moved in one step. That is about 8 times faster than moving each file and gives one Recycle Bin entry per folder. Everything else is moved file by file. Files that are in use are skipped, and the clean carries on. The result screen lists the skipped files and shows free space before and after.
5. After a Recycle Bin clean, **Empty Recycle Bin now** frees the space. It asks for its own confirmation first.

> Recycled files still take up space until the Recycle Bin is emptied. Byterake shows this on the confirmation screen and offers to empty the bin when it's done.

## The catalog (`catalog.json`)

Cleanup targets live in [`catalog.json`](catalog.json). Every field is explained in comments at the top of the file. To change targets without rebuilding, put an edited `catalog.json` next to `Byterake.exe`. The install folder has `catalog.example.json` to start from; copy it to `catalog.json` and edit it. Editing it inside Program Files needs admin rights. If that file is missing or broken, Byterake uses the built-in copy and says so on the Overview page.

Built-in targets:

| Target | Risk | Admin |
|---|---|---|
| User temp files (`%TEMP%`) | Safe | No |
| Windows temp files (`%WINDIR%\Temp`) | Safe | Yes |
| Recycle Bin | Safe | No |
| Chrome, Edge and Firefox caches (cache folders only) | Safe | No |
| Thumbnail cache (`thumbcache_*.db`, `iconcache_*.db`) | Safe | No |
| App crash dumps and error reports | Safe | No |
| System crash dumps and error reports | Safe | Yes |
| Windows Update downloads (`SoftwareDistribution\Download`) | Safe | Yes |
| Delivery Optimization cache | Safe | Yes |
| npm, pip and NuGet caches | Safe | No |
| Windows.old (handed to Windows Disk Cleanup) | Review | Yes |
| Old installers in Downloads | Review | No |
| Large files over N MB | Review | No |

## The never-touch list

This list is built into [`PathSafety.cs`](src/Byterake.Core/PathSafety.cs). Every file is checked against it again right before it is deleted.

- `C:\Windows\System32`, `SysWOW64`, `WinSxS`
- `Program Files` and `Program Files (x86)`, including everything inside
- `pagefile.sys`, `hiberfil.sys` and `swapfile.sys`, on any drive
- Your Documents, Desktop, Pictures, Videos and Music folders, including OneDrive-redirected ones
- Any path with a folder starting with `.git` (`.git`, `.github`, `.gitignore`)
- Any file with the System attribute, and any symbolic link or junction (never followed)
- Drive roots and top-level folders such as `C:\Windows`, `C:\Users` and your profile folder (the folder itself, not what's inside)
- Anything outside the selected catalog paths, or outside the folders of files you ticked

Paths are normalised before checking: environment variables are expanded, `..` is resolved, `/` becomes `\`, the `\\?\` prefix and trailing slashes are removed, case is ignored, and 8.3 short names like `PROGRA~1` are expanded. If a path is ambiguous (relative, or uses an environment variable that isn't set), it is refused.

## Build from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download) (download it once; building does not need the internet after that).

```powershell
.\build.ps1
```

This runs all tests, publishes `dist\Byterake.exe` (self-contained, single file, compressed, about 59 MB), then builds `Output\Byterake_Setup_v<version>.exe` with [Inno Setup](https://jrsoftware.org/isinfo.php) if it is installed. To build by hand:

```powershell
dotnet test tests\Byterake.Tests
dotnet publish src\Byterake.App -c Release -o dist
ISCC.exe installer\installer.iss
```

To try the light theme without changing Windows settings: `$env:BYTERAKE_THEME="light"; .\dist\Byterake.exe`

## Project layout

```
catalog.json                 cleanup targets (also built into the exe)
build.ps1                    test + publish + installer
installer/installer.iss      Inno Setup script
src/Byterake.Core/           no UI: scanner, catalog, path safety, cleaner, log, uninstall list
src/Byterake.App/            WPF app (light/dark themes follow Windows)
tests/Byterake.Tests/        xUnit tests (path safety, scanner sizes, cleaner, catalog, uninstall parsing)
tools/make-icon.ps1          redraws the app icon
```

## Tests

`dotnet test tests\Byterake.Tests` runs 119 tests:

- **Path safety (~80 cases).** Every never-touch rule, with different capitalisation, trailing slashes, `/` vs `\`, `\\?\` prefixes, `..` tricks, trailing dots and spaces, environment variables that expand into protected folders (including a made-up variable pointing at System32), 8.3 short names, junctions and System-attribute files.
- **Scanner.** Folder sizes on a temporary test tree, sort order, largest and large files, junctions and junction loops not followed, unreadable folders logged and skipped, cancelling.
- **Cleaner.** Only listed files are removed, files outside the roots are refused, protected paths are refused, nothing is reached through a junction, locked files are skipped while the rest continue, Recycle Bin mode works, and missing files are skipped quietly. Whole-folder moves happen only for folders holding exactly the confirmed files: never for a folder with an unconfirmed file or a junction, and a locked file inside is skipped while its neighbours still go. A speed check is included.
- **Catalog and uninstall.** Required targets and flags, bad entries dropped, a broken file falls back to the built-in copy, and uninstall command parsing.

Several tests really use the Recycle Bin, so each test run leaves about 2,000 tiny test files (in a few folders, about 200 KB in total) in your bin.
