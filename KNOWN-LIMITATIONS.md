# Known limitations

Byterake 1.0 was tested on Windows 11 Home, both with standard rights and as administrator. This page lists what could not be tested and what to expect.

## Not tested yet
- **Windows.old.** The test PC had no previous Windows install, so the hand-off to Windows Disk Cleanup (`cleanmgr.exe /d C:`) is untested. In Disk Cleanup, tick "Previous Windows installation(s)". If a future Windows release removes `cleanmgr.exe`, use Settings > System > Storage > Temporary files instead.
- **Firefox cache.** Firefox was not installed on the test PC, so the `%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\cache2` path was not checked against a real profile.
- **Delivery Optimization cache.** On the test PC, Delivery Optimization was set by policy to download only from Microsoft (`CdnOnly`), so it kept no cache and nothing could be deleted. Both cache locations used by Windows 10/11 are in the catalog.

## Good to know
- **Close your browser before cleaning its cache.** Chrome, Edge and Firefox lock cache files while they run, and Byterake skips files that are in use (it lists them when the clean finishes).
- **Thumbnail cache.** Windows Explorer keeps some `thumbcache_*.db` and `iconcache_*.db` files open, so these are usually skipped as "in use".
- **The Recycle Bin.** Files moved to the Recycle Bin still use disk space until the bin is emptied. Byterake shows this on the confirmation screen and offers **Empty Recycle Bin now** when it is done.
- **Very large files.** If a file is too big for the Recycle Bin, Windows shows its own "delete permanently?" prompt instead of deleting it silently.
- **Recycle Bin item count.** The confirmation screen shows how many items are in the bin at that moment. Anything added to the bin in the second or two between confirming and cleaning is emptied too.
- **Folder sizes are file sizes added up.** Compressed files, sparse files and hard links (common in `WinSxS`, which is protected anyway) can make totals larger than the space really used.
- **Browser cache paths can change.** Chrome and Edge occasionally move their cache folders between versions. Paths that don't exist are skipped.
- **Unsigned app.** Byterake isn't code-signed, so Windows SmartScreen may say "Windows protected your PC" the first time you run the installer. Choose **More info → Run anyway**.
