# ohmyZip Help

ohmyZip is an archive manager for Windows. You can open an archive to see what's inside, extract it, test it, create new archives, and change the items in an archive.

## Requirements and Starting ohmyZip

- Windows 10 or 11 (x64)
- .NET Desktop Runtime 9 — if it isn't installed, install ".NET Desktop Runtime 9" from the official Microsoft website.
- Run `ohmyZip.exe` in the installation folder. If you pass an archive path as the first argument, ohmyZip opens that archive right away.
- Settings file: `%APPDATA%\ohmyZip\settings.json` (or the folder set in the `OHMYZIP_DATA_DIR` environment variable). Passwords are never saved.

## Features

- **Open**: Ctrl+O, drag a file onto the window, or pass it on the command line. The format is detected from the file's contents (its first bytes).
- **Password-protected archives**: If a password is needed while opening, testing, extracting, or adding, the password window appears. If the password is wrong, ohmyZip says so ("The password is incorrect") and asks again. A password is kept in memory only for the open archive.
- **List**: Folder tree on the left (F7), file list on the right. Click a column header to sort (folders first, with an ascending/descending indicator); right-click a column header to choose columns.
- **List style**: View > File List > Details / List / Icons / Large Icons.
- **Columns**: Packed Size, Size, Type, Date Modified, Method, Encryption, CRC, Attributes, Description, Extra Info. Columns that the engine doesn't provide values for (Attributes, Description, Extra Info) are empty.
- **Find**: Press Ctrl+F to go to the search box. ohmyZip searches names in the whole archive (all folders) and shows the full path of each result. You can use the `*` and `?` wildcards; if you include `/`, the whole path is matched. Enter = go to the folder that contains the item and select it, Esc = clear.
- **Extract**: Press Ctrl+E to open the Extract window. Choose the destination (Browse, create a new folder), what to extract (all items / selected items), what to do when a file with the same name exists (Ask / Replace / Skip / Rename), and whether to open the folder afterward.
- **Smart Extract**: On the File menu, or the button next to Extract on the toolbar. If the archive has a single top-level item, ohmyZip extracts it directly into the folder that contains the archive; otherwise it extracts into a folder named after the archive (or "Name (2)" if that folder already exists).
- **Safe extraction**: Items whose paths or links point outside the destination folder are refused, and extraction stops if it would exceed 8 GB in total or 200,000 items. If you cancel, files created by the operation are removed and files that were being replaced are restored.
- **Create an archive**: Ctrl+N → select files → New Archive window. Add folders, choose where to save, the format (ZIP, 7Z, TAR, TAR.GZ, GZ), the compression level (0 store … 9 best), a password (AES-256) with file name encryption (7Z), and the split (volume) size. Options the format doesn't support are turned off, with the reason shown next to them. When the archive is created, ohmyZip opens it. If creation fails or is canceled, the partial file isn't kept.
- **Change items**: Add Files (Ins), Delete Files (Del), Rename (F2). Changes are written to a temporary file and applied only when they succeed, so if an operation fails or is canceled, the archive stays as it was.
- **Clipboard**: Ctrl+C = extract the selected items to a temporary folder and copy them as files (paste them in File Explorer), Ctrl+V = add the files on the clipboard to the current folder, Copy File Path to Clipboard = a list of `archive path\item path` lines. The temporary folder is deleted when you close the window.
- **Archive comment**: Press F8 to show the comment pane, and Ctrl+Shift+M to edit the comment (only when the format supports comments).
- **Progress window**: Amount processed/total, percentage, speed, and time remaining. If the total size is unknown, it shows "Progress unknown". Cancel with the Cancel button or Esc.
- **File operations**: Save As… (saving to the same path is refused), Delete Archive (Ctrl+Del, moves it to the Recycle Bin), Open Containing Folder, New Window.
- **Settings… (F5)**: General (start screen, theme, reduce motion, automatic updates), File Associations, File Explorer Menu, Extract (default location, what to do with existing files, open folder), Create Archives (default format and compression level), View & Edit (confirm before deleting, list style, columns), Language. OK = save and apply, Cancel = change nothing, Reset… = restore all settings to their defaults.
- **File associations (default app)**: Settings… > File Associations > Register ohmyZip as Default App… → review what will be written and click Register → in the Windows Default Apps settings that open, choose ohmyZip to make it the default app. Windows doesn't let apps change the default app themselves, so you make the final choice. The extensions are .zip .7z .rar .tar .gz .tgz. ohmyZip writes only to the current user's scope (HKCU), so no administrator rights are needed, and it doesn't change the extensions' current default app setting. To undo, click Unregister on the same page. The buttons on this page take effect immediately, regardless of OK or Cancel. If you move the program folder, register again. Command line: `ohmyZip.exe --register-file-types` / `ohmyZip.exe --unregister-file-types` (runs without a window; exits with 0 on success and 1 on failure).
- **File Explorer right-click menu**: Settings… > File Explorer Menu > Register… → review what will be written and click Register. When you right-click a file or folder in File Explorer, an "ohmyZip" submenu appears. On Windows 11, it's still under Show more options (Shift+F10) after you right-click. Archives (.zip .7z .rar .tar .gz .tgz) get seven items (Open, the five extract items, and the two compress items); any other file or folder gets the two compress items (Compress… and Compress to ZIP).
  - Open = open the archive in the ohmyZip window.
  - Extract… = open the Extract window to choose the destination, what to extract, and what to do with existing files.
  - Extract Here = extract directly into the folder that contains the archive.
  - Extract to Folder (Archive Name) = extract into a folder named after the archive, inside the folder that contains it (`a.zip` → `a`). If a folder with that name already exists, ohmyZip extracts into it.
  - Smart Extract = if the archive has a single top-level item, extract it directly into the folder that contains the archive; otherwise extract into a folder named after the archive (or "Name (2)" if that folder already exists).
  - All extract items: ohmyZip opens a window, opens the archive (asking for a password if needed), and closes the window when extraction ends (including when it's canceled or fails). The three items that extract without asking (Extract Here, Extract to Folder, Smart Extract) follow the conflict handling and "Open folder after extracting" in Settings… > Extract.
  - Compress… = open the New Archive window with the selected files and folders. The default destination is `name.zip` in the folder that contains the first selected item (the item's name if you selected one, otherwise that folder's name); you can change the format, password, and splitting in the window. When the archive is created, ohmyZip opens it in its window; if you cancel or it fails, the window closes.
  - Compress to ZIP = create `name.zip` in the folder that contains the selected items without asking. The name is the item's name if you selected one (without the extension for a file), otherwise that folder's name; if the name is taken, ohmyZip uses `name (2).zip`, `name (3).zip`, and so on. The compression level is the default level in Settings… > Create Archives. A progress window is shown, and the window closes when it's done. If the selected items are in different folders, ohmyZip doesn't create anything and tells you why (use Compress… instead).
  - If you select several items, ohmyZip runs only once: the extract items extract the selected archives one after another, Open opens the first archive in that window and each of the others in a new window, and the compress items put all the selected items into one archive.
  - ohmyZip writes only to the current user's scope (HKCU), so no administrator rights are needed, and file associations aren't changed. Menu text is written in the display language at the time you register, so register again after changing the language or moving the program folder. To turn the menu off, click Unregister on the same page. The buttons on this page take effect immediately, regardless of OK or Cancel. Command line: `ohmyZip.exe --register-shell-menu` / `ohmyZip.exe --unregister-shell-menu` (runs without a window; exits with 0 on success and 1 on failure). The menu items are run by the menu command handler in the program folder (`ShellCommand.dll`), which writes the whole selection to a list file (in `%LOCALAPPDATA%\ohmyZip\selections\`) and runs a command like `ohmyZip.exe --extract-here --list "list file"` (ohmyZip deletes the list file after reading it, and deletes lists left over for more than a day the next time it starts). On the command line you can also pass paths directly: `ohmyZip.exe --compress-zip "file1" "file2"`. The arguments are `--open` (Open; `ohmyZip.exe "archive"` without an argument does the same), `--extract` (Extract…), `--extract-here`, `--extract-to-folder`, `--extract-smart`, `--compress` (Compress…), and `--compress-zip` (Compress to ZIP; exits with 0 on success and 1 on failure).
- **View**: Toolbar Large / Small / Classic (text only) / Hide, Customize Toolbar…, Status Bar, Start Screen (F6), Always on Top, Color Theme… (Light/Dark within the app).

## Updates

New versions come from the releases of the GitHub repository (`woori-doweb/ohmyzip`). A downloaded archive is installed only if it matches the SHA-256 value published with it.

- **Automatic check**: When the ohmyZip window opens, ohmyZip checks for a new version once a day and, if there is one, quietly downloads it in the background. When the download finishes, the status bar says "Version … has been downloaded". If you aren't connected to the internet or the check fails, nothing is shown and ohmyZip checks again next time.
- **Install on close**: A downloaded new version is copied into the installation folder when you close ohmyZip. The new version starts the next time you run it. If installation doesn't complete, the installation folder is restored and the update is downloaded again on the next run. Old copies of replaced files (`*.ohmyzip-old`) are deleted on the next run.
- **Update now**: Help > Check for Updates…. If you have the latest version, the status bar says so; if a new version is available, ohmyZip shows the current and new versions and what's new (the commit history). Click Update to download it in a progress window (you can cancel), install it, and restart ohmyZip with the same archive argument.
- **Turn off**: In Settings… (F5) > General, turn off "Download new versions automatically and install when ohmyZip closes". When it's off, ohmyZip doesn't check or install automatically; you update only with Help > Check for Updates….
- **When the installation folder isn't writable**: If ohmyZip is installed in a folder you don't have write permission for, such as Program Files, ohmyZip tells you it can't update. Run ohmyZip as administrator and update again, or move it to your user folder and install it there.
- Working files are kept in `%LOCALAPPDATA%\ohmyZip\updates`. You can see the current version in Help > About ohmyZip (F1).

## Keyboard Shortcuts

| Key | Action |
|---|---|
| Ctrl+O | Open Archive |
| Ctrl+N | New Archive |
| F4 | Close Archive |
| Ctrl+Del | Delete Archive (Recycle Bin) |
| Ctrl+T | Test Archive |
| Ctrl+E | Extract |
| Ctrl+Shift+N | New Window |
| Ctrl+W | Exit |
| F2 | Rename |
| Ins | Add Files |
| Del | Delete Files |
| Ctrl+Shift+M | Edit Archive Comment |
| Ctrl+C / Ctrl+V | Copy to / Paste from Clipboard |
| Ctrl+F | Find Files |
| F5 | Settings |
| F6 | Start Screen |
| F7 | Folder Tree |
| F8 | Archive Comment pane |
| F1 | About ohmyZip |
| Backspace | Up one level |
| Enter | Open folder / Go to find result |

## Coming Soon and Unsupported Features

These are shown on the menus with "(Coming Soon)" and can't be clicked.

- Secure Delete Archive, Scan Archive for Malware
- New Window (Administrator)
- Register Product / Unregister Product
- Password Manager (Choose from Password Manager in the password window is also coming soon)
- Preview Image Files
- Repair Archive, Recover Password
- Open Discussion Forum, Open Website
- In Settings: Security and Advanced
- Languages: only English and Korean are available. ohmyZip follows the Windows display language; you can choose a language in Settings… > Language. When you change the language and click OK, ohmyZip asks whether to restart now (if you choose Later, the new language appears the next time you start ohmyZip).
- Format-specific features (create, password, split, comment, modify) are turned on only as far as the engine supports them. When a feature isn't supported, its menu item and button are turned off and the reason appears in the tooltip.
