# GUI Smoke Checklist (Avalonia shell)

Goal: quick confidence pass before handing the Windows build to reviewers.

1. **Launch**
   - `dotnet run --project gui/DriftBuster.Gui/DriftBuster.Gui.csproj`
   - Confirm the DrB red/black window appears with Diff default view.

2. **Ping core**
   - Click the **Ping Core** button in the title strip.
   - Verify the Hunt view loads with a positive status message (`Ping reply: pong`).

3. **Diff workflow**
   - Navigate back to **Diff**.
   - Use the **Browse** buttons to pick two sample files (e.g. `fixtures/sample.md` vs itself).
   - Ensure validation removes the warning once both files exist.
   - Click **Build Plan**.
   - Expect the plan/metadata tables to populate and the raw JSON expander to light up.
   - Use **Copy raw JSON** and confirm clipboard contents match the displayed JSON.

4. **Hunt workflow**
   - Switch to **Hunt**.
   - Browse to a directory with mixed content (e.g. `fixtures/`).
   - Optionally add a filter substring (e.g. `server`).
   - Click **Scan**.
   - Confirm the hits table lists matches with rule, relative path, and excerpt columns.

5. **Error handling**
   - In Diff view, clear the right-hand file path and verify the warning + disabled run button.
   - In Hunt view, point at a non-existent directory; a validation message should block execution.

6. **Backend lifecycle**
   - Close the window.
   - Confirm no lingering error dialogs appear; all backend processing now runs in-process.

Record run outcomes and timestamps below when executing manually.

| Date (UTC) | Operator | Notes |
|-----------|----------|-------|
