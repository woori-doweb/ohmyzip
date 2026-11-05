# ohMypic Help

ohMypic browses, views and edits images on Windows. One window has two modes:

- **Browser**: folder tree on the left, the images of the folder in the middle, optional preview and camera info panes on the right.
- **Viewer**: one image fills the window. Press Esc to go back to the browser.

## Browsing

- Pick a folder in the folder tree, type a path in the address bar and press Enter, or use **File > Open Folder** (Ctrl+Shift+O).
- **View** chooses the view format: Thumbnails, Large Icons, Icons, List, Details or Filmstrip, and the sort order (name, type, size, date; ascending or descending).
- **View > Preview Pane** and **View > Camera Info Pane** show the selected image and its camera data.
- Double-click an image or press Enter to view it.
- Dropping a folder on the window opens it; dropping an image file opens it in the viewer.

## Viewing

- Next / previous image: Right / Left, Page Down / Page Up, Space, or the mouse wheel. First / last: Home / End.
- Zoom: + and -, or Ctrl + mouse wheel. 0 shows the actual size (one image pixel per screen pixel). * fits the image to the window.
- Drag a large image to move it. The navigator in the corner shows which part is visible.
- Full screen: F11 or Enter, or double-click the image. Esc leaves full screen, then returns to the browser.
- Rotate the view: Ctrl+] (right) and Ctrl+[ (left). When you move to another image or close the window, ohMypic asks whether to save the rotation (or saves it automatically, see Settings). JPEG files are rotated without quality loss when their size allows it.

## Slideshow

Press F5. Left / Right step, Space pauses and resumes, Esc ends the slideshow. Right-click for the slideshow menu. The time per image and the fade effect are in **Tools > Settings > Slideshow**.

## Editing

The **Image** menu (also the **Edit** button on the viewer toolbar) works on the viewed image or the selected images:

| Command | Keys |
|---|---|
| Rotate… | Ctrl+T |
| Resize… | Ctrl+R |
| Convert Format… | Ctrl+E |
| Batch Edit… (resize, rotate, rename and convert in one run) | Ctrl+M |

The result is shown in the status bar. A message appears only when some files failed.

## Files

| Command | Keys |
|---|---|
| Open File… | Ctrl+O |
| Save As… | Ctrl+S |
| Print… | Ctrl+P |
| File Properties | Alt+Enter |
| Copy / Cut / Paste files | Ctrl+C / Ctrl+X / Ctrl+V |
| Copy Image (picture to the clipboard) | Ctrl+Shift+C |
| Delete | Del |
| Rename… | F2 |
| Select All | Ctrl+A |
| Up one folder | Alt+Up or Backspace |
| Refresh | Ctrl+F5 |

Deleted files go to the Recycle Bin unless you change this in Settings.

## Settings

**Tools > Settings** (F4) has these pages: General (delete, hidden files, rotation saving, JPEG quality, language), View (fit mode, mouse wheel, looping, background, navigator), Slideshow, Thumbnails, File Associations and File Explorer Menu (see below). Settings are stored in `%APPDATA%\ohMypic\settings.json`; the window size and panes in `window.json` next to it.

Items marked "(Coming soon)" are planned for a later version.

## File associations and the File Explorer menu

Both change only your own Windows account (no administrator rights) and take effect immediately; OK and Cancel in Settings do not undo them.

- **Tools > File Associations…**: check the image types and press **Register**. ohMypic is then offered for those types in "Open with" and in Windows Settings > Apps > Default apps. Windows does not let a program make itself the default: press **Open Windows Default apps** and choose ohMypic there. **Unregister** removes everything ohMypic added; the current default app of each type is never changed.
- **Tools > File Explorer Menu…**: **Register…** shows the registry keys and program path first, then adds an **ohMypic** submenu to the right-click menu of image files: Open, Slideshow, Rotate…, Resize…, Convert Format… and Batch Edit…. On Windows 11 it is under "Show more options" (Shift+F10). The menu text uses the display language at the time of registration. **Unregister** removes the menu.

Command line (no window; exit code 0 = success, 1 = failure):

| Option | Effect |
|---|---|
| `--register-associations` / `--unregister-associations` | Register / remove the file associations |
| `--register-shell-menu` / `--unregister-shell-menu` | Register / remove the File Explorer menu |

The menu items start ohMypic with these options followed by the file path: `--slideshow` (slideshow of the file's folder, starting at the file), `--rotate`, `--resize`, `--convert` and `--batch` (only that edit dialog; ohMypic closes with it).

## Updates

When **Check for updates automatically** is on (**Tools > Settings > General**, on by default), ohMypic looks for a new release on GitHub at most once a day, downloads it in the background, verifies its SHA-256 checksum and installs it when you close ohMypic. **Help > Check for Updates…** shows your version, the new version and its release notes; **Update and Restart** installs it right away and reopens the current image or folder. Only the files in the ohMypic folder are replaced; settings are kept.

## Supported formats

Opens BMP, GIF, JPEG, PNG, TIFF, ICO, JPEG XR and DDS, plus any format with an installed Windows imaging decoder. Saves BMP, GIF, JPEG, PNG and TIFF.
