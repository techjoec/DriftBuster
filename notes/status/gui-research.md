# Windows GUI Research Status

## Decisions & Findings

- Framework shortlist: WinUI 3, Tkinter, PySimpleGUI, Electron. Electron reserved for rich HTML workflows; Tkinter/PySimpleGUI for lightweight builds.
- Packaging baseline: MSIX for WinUI/Electron; portable zip with bundled Python runtime for Tkinter/PySimpleGUI.
- Manual update cadence until signing/auto-update story is approved.

## Open Questions

- Which framework aligns best with the eventual reporting pipeline timeline?
- Do we pre-provision WebView2 Evergreen or rely on per-host install scripts?
- What accessibility tooling (Narrator, Inspect) should form the test baseline?

## Next Actions (Deferred)

- Prototype data loading adapters once reporting outputs stabilise.
- Draft NOTICE/licence bundle templates before distributing binaries.
- Plan VM matrix for manual verification (Windows 10/11, offline/online hosts).

## User Requirements

- Offline-ready packaging must ship alongside the first GUI preview so security teams can sideload builds without network access (see [Packaging & Distribution Plan](../../docs/windows-gui-notes.md#packaging--distribution-plan)).
- The GUI needs a lightweight viewer mode that reads generated HTML diffs without bundling new scanners, keeping CLI ownership intact (see [Data Flow & UX Outline](../../docs/windows-gui-notes.md#data-flow--ux-outline)).
- Accessibility pass requires Narrator and Inspect coverage before the HOLD lifts; track mitigation steps against the [Compliance & Accessibility Checklist](../../docs/windows-gui-notes.md#compliance--accessibility-checklist).
- Decision on WebView2 Evergreen redistribution is pending to finalise installer prerequisites (see [Candidate Frameworks](../../docs/windows-gui-notes.md#candidate-frameworks)).
