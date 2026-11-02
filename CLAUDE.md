# SysStateTracer — notes for Claude Code

Read `docs/chat-history.md` first: goals, architecture, decisions, what is open (GitHub issues #6–#9) and the
conventions agreed with the owner (Igor Zaharov). Decision log: `docs/decisions.md`. Requests: `docs/featurerequest.md`.

Essentials:

- Windows 10/11 x64 WinForms app, .NET 10, self-contained single-file exe; solution `SysStateTracer.slnx`.
  Build: `dotnet build SysStateTracer.slnx -c Release`; test: `dotnet test SysStateTracer.slnx -c Release`.
- Warnings are errors. Write private-use glyphs escaped (`""`), never raw.
- Work on `main`; the owner pushes too: fetch and rebase before every push. CI (`build.yml`) must be green.
- Never run anything on the owner's PC or VM without asking. Snapshots hold sensitive registry data.
- Every user-visible change: `CHANGELOG.md` (Unreleased), `docs/user-interface.md` or `docs/command-line.md`,
  `tools/manual/make_manual.py`, README screenshots from the `ui-preview` pre-release when windows change.
- Done requests are marked `[DONE in vX.Y.Z]`; closing issues and publishing releases are the owner's call.
- Releases: dated CHANGELOG section + `VersionPrefix`, green CI, then run the `release` workflow with the version.
