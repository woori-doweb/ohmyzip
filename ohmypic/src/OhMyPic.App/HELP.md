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

- Next / previous image: Right / Left, Page Down / Page Up, Space. First / last: Home / End.
- Mouse wheel: zooms in and out at the pointer. Ctrl + mouse wheel goes to the next / previous image. **Tools > Settings > View > Mouse wheel** swaps the two.
- Zoom: + and -. 0 shows the actual size (one image pixel per screen pixel). * fits the image to the window.
- Drag a large image to move it. The navigator in the corner shows which part is visible.
- Full screen: F11 or Enter, or double-click the image. Esc leaves full screen, then returns to the browser.
- Rotate the view: Ctrl+] (right) and Ctrl+[ (left). When you move to another image or close the window, ohMypic asks whether to save the rotation (or saves it automatically, see Settings). JPEG files are rotated without quality loss when their size allows it.
- Right-click the image for a menu with Full Screen, Slideshow, Smooth Image, Image Placement (the same choices as **When an image opens** in **Tools > Settings > View**), Copy Image, Save As, Rotate / Resize / Convert Format, Print and Properties.

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

## Editor

**Image > Open in Editor** (Ctrl+Shift+E, also in the right-click menus) opens one image in a separate editor window. Everything stays on this computer.

- **Crop**: Free, 1:1, 4:3, 16:9, 9:16 or 4:5; drag the frame, then **Apply** (Enter) or **Cancel** (Esc).
- **Rotate and flip**: 90° right (Ctrl+]) and left (Ctrl+[), 180°, horizontal and vertical flip.
- **Adjust**: brightness, contrast, saturation, warmth, hue, sharpen, vignette, black & white and sepia; **Reset** sets them all to 0. Eight presets apply a set of values at once.
- **Draw**: Pen, Rectangle or Arrow in six colors and three widths; drag on the picture. Click the selected tool again to stop drawing.
- **Copy image** (Ctrl+C) puts the edited picture on the clipboard, ready to paste into a chat or document. **Copy as file path** (Ctrl+Shift+C) saves it as a PNG in Pictures\Screenshots and copies the file path instead (an unchanged file copies its own path).
- **Undo** Ctrl+Z, **Redo** Ctrl+Y or Ctrl+Shift+Z.
- **Export…** (Ctrl+Shift+S): PNG or JPG, JPG quality, scale 0.5×–3×, with the final size and the estimated file size before saving. Choosing the original file asks before replacing it.

Closing the editor asks only when there are changes that were not exported. Layers, text, brushes and the other tools marked "(Coming soon)" are not available yet.

## Print Screen capture

Turn on **Tools > Settings… > General > Capture with the Print Screen key and open the editor**. ohMypic then keeps running in the notification area and starts when you sign in. Print Screen captures the monitor under the mouse pointer and opens it in the editor with the red pen ready: mark it up, press Ctrl+C, paste. Esc closes the capture window (it asks only when there are changes you did not copy or export). Alt+Print Screen, Windows+Print Screen and Windows+Shift+S keep their Windows behavior. The notification area icon has **Capture screen** and **Turn off Print Screen capture**.

## Fonts

**Tools > Fonts…** lists the fonts installed on this computer with a preview sentence and size. Search by name, show only fonts that include Korean (Hangul) characters, or only favorites. Click the star or press Space to mark a favorite; favorites are kept after restarting. Ctrl+C copies the font name.

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
