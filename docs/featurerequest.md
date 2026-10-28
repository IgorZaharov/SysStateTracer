Requested Features:
1. [DONE in v1.0.0-rc.1] In Preferences window add radio button selector on selecting Global ignore list location: Remote (edit box, should support both network fileshare or URL) or Local. Local is looking beside .exe. If remote one is not accessible - local is used. Default remote location should be set to global ignore list file on this GitHub repository.
2. [DONE in v1.0.0-rc.1] Application should have icon. Generate .ico file for application: small HDD icon on black bakground with green and blue horizontal lines overlapping on the right side, imitating filenames. Application icon should be available in small and big detailed view in explorer.
3. [DONE in v1.0.0-rc.1] Possibility to Export whole tree of files and folders by clicking Export on top level directory. Folders and files marked as non-important are ignored by export.
5. [DONE in v1.0.0-rc.1] UI: Preferences > General window - add icons for themes above Follow Windows/Light/Dark.
6. [DONE in v1.0.0-rc.1] Command-line switches: Snapshot names could have .stsnap extension ("test 1.stsnap") or path ("C\Test\snapshot1.stsnap").
7. [DONE in v1.0.0-rc.1] Command-line switches: /export should also create .ps1 script under .\Files to copy all folders/subfolders/files back to their place (e.g.: .\Files\C\Test\test1.txt > to C:\Test\test1.txt).
8. [DONE in v1.0.0-rc.1] Generate professional looking Manual.pdf with screenshots: Title page - utility name and description, table of contents: Introduction (description and links to GitHub), Common usage using UI, Command-line usage, Editing Global Ignore List, Preferences.
9. [DONE in v1.0.0-rc.2] Evaluate and add possibility to run SysSnapTracer under standard limited User, knowing it's restrictions. Grey out folders/registry that is impossible for user to read (e.g. under HKU/USERS/...) during New Snapshot creation. Get rid of UAC prompt at start if possible.
10. [DONE in v1.0.0-rc.2] Comparing snapshots window: should show created registry values, not only keys - when subkey is expanded.
11. [DONE in v1.0.0-rc.2] Comparing snapshots window: should show created files, not only directory - when last subfolder is expanded.
12. [DONE in v1.0.0-rc.2] Bugfix UI: Windows state/size/positions are not saved. Resized windows return to defaults when Application closed.
13. [DONE in v1.0.0-rc.2] Bugfix UI: Preferences > General - when clicking on Theme Icon - it selects radiobutton, but does not deselect other ones. It is possible to click all three themes and it will select all three radiobuttons. Only 1 radiobutton may be selected at any given time.
14. [DONE in v1.0.0-rc.2] UI: main window initial size should be 850*450px, all other windows - no more than 580px by vertical.
15. [DONE in v1.0.0-rc.2] Bugfix UI: Main window - when mouse cursor is few pixels between rows other columns showing blank values.
16. [DONE in v1.0.0-rc.2] UI: Main window - remove "Sort by" drop-down selection and add possibility to sort by pressing on Column headers. Default remains as is - by date, newest on top.
17. [DONE in v1.0.0-rc.2] GitHub: README.MD should have easily visible and accessible direct link on latest version download for .exe and manual for people without GitHub UI knowledge.
18. [DONE in v1.0.0-rc.2] Bugfix: Snapshot Comparison window: when User presses "Add to Global Ignore list" - the whole comparison collapses and User needs to expand it back manually. Should stay expanded as it was.
19. [DONE in v1.0.0-rc.2] UX: Snapshot Comparison window: remove tooltips on mouse hover of items. It blocks the row.
20. [DONE in v1.0.0-rc.2] UX: Add to Global Ignore list dialog from Snapshot Comparison right-click: must be able to modify entry (e.g. by adding "*").
21. [DONE in v1.0.0-rc.2] UX: Better differentiate visually Folders/Files in Snapshot Comparison table. Will adding small directory icon in front of directory name have impact on performance?
22. [DONE in v1.0.0-rc.2] UX: Changes done to local ignore list are not applied if set to use Remote list. By default should use both, radio button ignore list location selection in Preferences should be removed, logic changed to "use both" or "local only".
23. [DONE in v1.0.0-rc.2] Bugfix: Snapshot Deletion dialog - Enter, Left, Right should work immediately.
24. [DONE in v1.0.0-rc.2] Bugfix: New Snapshot window - Enter, Left, Right should work immediately. Enter should start new Snapshot creation.
25. [DONE in v1.0.0-rc.2] Preferences > Global Ignore list - should be possible to modify/edit entries without going outside of Application.
26. [DONE in v1.0.0-rc.2] Bugfix: Global Ignore List - wildcards not working in File system paths.
27. [DONE in v1.0.0-rc.2] UX: Snapshot Comparison window: green/red/yellow fonts on added/deleted/modified entries must be in all columns (e.g.: date, size, values. etc.).
28. [DONE in v1.0.0-rc.2] GitHub: README.MD - after bar with download links and before first screenshot - add instruction to right-click downloaded .exe, press Properties, Unblock and press OK. Add screenshot of .exe file details with "Unblock" checkbox ticked.
29. [DONE in v1.0.0-rc.2] UX: On Snapshot Comparison window - first column on the left should be glyph whether item deleted/added/changed.
