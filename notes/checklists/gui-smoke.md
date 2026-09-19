# GUI Smoke Checklist (Avalonia shell)

Goal: a confidence pass over every page before handing a Windows build to reviewers. Run it in Dark+ and again in Light+.
Use two or more copies of a configuration tree that differ (the `fixtures/multi-server/` folders work) as the hosts.

1. **Launch**
   - Run `DriftBuster.Gui.exe` from the release zip (or `dotnet run --project gui/DriftBuster.Gui/DriftBuster.Gui.csproj`).
   - The window opens on **Multi-server**; the core dot turns green after **Check core**.

2. **Setup**
   - Select each host in the list; the editor on the right follows. Add a root, remove it, change the scope.
   - **Add host** appends and selects a new host; untick it so it is not scanned.
   - Drag a host to a new position; the order holds after a restart when **Remember session** is on.
   - Add a key under **Registry keys (all hosts)** (e.g. `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion`); on one host
     set **Computer** to another machine and try **Save sign-in…**, **Use a saved file…** and **Use my sign-in**.

3. **Compare**
   - **Run all**; the page lands on Compare with one chip per server and the file list.
   - **Next** / **Previous** (F8 / Shift+F8) walk the differences into the next file; right-clicking a row does not move the grid.
   - Click a server chip, search, toggle **Only show differences**, **Show: Marked**, **Show ignored**.
   - The registry key is listed as `registry/HKLM/…/.reg` with one row per value; File details shows its `.reg` text.

4. **Right-click menu** (on a setting, a value and a file)
   - Group: add (new name), view, remove. Rule: create from the item with an application name, add another setting to it.
   - Mark, Copy as each format (check the clipboard), View → As tree and Raw data, History (all three tabs).
   - Add to report, then **Review list** and **Export review**; remove it again.
   - Ignore a setting (this run), a value (always) and a file (these servers): the chip counts drop; **Show ignored** shows them dimmed; **Forget this run's choices**.
   - Mask and unmask values; a restart keeps the saved choice and drops the this-run one.
   - Report bug: the send buttons stay disabled until the check is ticked; a secret shown unmasked on screen still goes out masked.

5. **Manage choices**
   - Every saved group, rule, choice and review item is listed; remove one and **Save**; **Export…** writes a file; **Clear scan history…** asks first.

6. **Files and File details**
   - Right-click a row: **Show settings in Compare** lands on that file; **File details** opens the line diff.
   - In File details, switch **Compared with** between servers, use **Next change** (F7), and **Side by side** / **Unified**.

7. **Diff planner**
   - Pick a baseline and two other files, **Build plan**; the inputs fold away and the Settings tab opens.
   - Line by line and JSON tabs show the comparison; right-click works in the Settings tab.

8. **Hunt explorer**
   - Scan a folder; click a rule chip, search, right-click a finding (copy location, show only this file, report false positive, open folder).

9. **Profiles**
   - Load a saved profile; the actions stay pinned while the form scrolls.

10. **Close**
    - Close the window; no error dialogs appear and no DriftBuster process remains.

Record run outcomes in the release evidence, not in this file.
