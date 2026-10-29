"""Builds Manual.pdf (feature request F8) from the window screenshots the UI tests save.

CI runs it after the tests, with the screenshots of that very build and the text of `SysStateTracer.exe /?`:

    python tools/manual/make_manual.py --screens screenshots --help-text help.txt --version 1.0.0 --out Manual.pdf

Only the light-theme screenshots are used, plus one dark one. Missing screenshots are left out with a note,
so the manual can also be built from an older set.
"""

from __future__ import annotations

import argparse
import datetime as dt
import os
import re
from pathlib import Path

from PIL import Image as PilImage
from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER, TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import (BaseDocTemplate, CondPageBreak, Frame, Image, KeepTogether, NextPageTemplate,
                                PageBreak, PageTemplate, Paragraph, Preformatted, Spacer, Table, TableStyle)
from reportlab.platypus.tableofcontents import TableOfContents

GITHUB = "https://github.com/IgorZaharov/SysStateTracer"
RELEASES = GITHUB + "/releases"
LATEST = RELEASES + "/latest/download"
ISSUES = GITHUB + "/issues"
REPO = Path(__file__).resolve().parents[2]
ACCENT = colors.HexColor("#005FB8")
TEXT = colors.HexColor("#1B1B1B")
SECONDARY = colors.HexColor("#616161")
RULE = colors.HexColor("#E0E0E0")
SURFACE = colors.HexColor("#F3F3F3")
NEW = colors.HexColor("#0F7B0F")
MODIFIED = colors.HexColor("#9D5D00")
DELETED = colors.HexColor("#C42B1C")
INSIDE = colors.HexColor("#0060C0")
PAGE_W, PAGE_H = A4
MARGIN = 20 * mm
CONTENT_W = PAGE_W - 2 * MARGIN

# ---------------------------------------------------------------- fonts

FONT_CANDIDATES = {
    "Body": [r"C:\Windows\Fonts\segoeui.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"],
    "Body-Bold": [r"C:\Windows\Fonts\segoeuib.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"],
    "Body-Italic": [r"C:\Windows\Fonts\segoeuii.ttf", r"C:\Windows\Fonts\segoeui.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans-Oblique.ttf"],
    "Heading": [r"C:\Windows\Fonts\seguisb.ttf", r"C:\Windows\Fonts\segoeuib.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"],
    "Light": [r"C:\Windows\Fonts\segoeuil.ttf", r"C:\Windows\Fonts\segoeui.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans-ExtraLight.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"],
    "Mono": [r"C:\Windows\Fonts\consola.ttf", r"C:\Windows\Fonts\cour.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf"],
}


def register_fonts() -> None:
    for name, candidates in FONT_CANDIDATES.items():
        path = next((p for p in candidates if os.path.exists(p)), None)
        if path is None:
            raise SystemExit(f"No font found for {name}: {candidates}")
        pdfmetrics.registerFont(TTFont(name, path))
    pdfmetrics.registerFontFamily("Body", normal="Body", bold="Body-Bold", italic="Body-Italic", boldItalic="Body-Bold")


# ---------------------------------------------------------------- styles

def styles() -> dict[str, ParagraphStyle]:
    s: dict[str, ParagraphStyle] = {}
    s["body"] = ParagraphStyle("body", fontName="Body", fontSize=10, leading=14.5, textColor=TEXT, spaceAfter=6)
    s["small"] = ParagraphStyle("small", parent=s["body"], fontSize=8.5, leading=11.5, textColor=SECONDARY)
    s["caption"] = ParagraphStyle("caption", parent=s["small"], alignment=TA_CENTER, spaceBefore=3, spaceAfter=12)
    s["h1"] = ParagraphStyle("h1", fontName="Heading", fontSize=20, leading=25, textColor=ACCENT, spaceBefore=0, spaceAfter=10)
    s["h2"] = ParagraphStyle("h2", fontName="Heading", fontSize=13.5, leading=18, textColor=TEXT, spaceBefore=12, spaceAfter=5)
    s["h3"] = ParagraphStyle("h3", fontName="Body-Bold", fontSize=10.5, leading=14, textColor=TEXT, spaceBefore=8, spaceAfter=3)
    s["bullet"] = ParagraphStyle("bullet", parent=s["body"], leftIndent=14, bulletIndent=3, spaceAfter=3)
    s["cell"] = ParagraphStyle("cell", parent=s["body"], fontSize=9, leading=12.5, spaceAfter=0)
    s["cellhead"] = ParagraphStyle("cellhead", parent=s["cell"], fontName="Body-Bold", textColor=SECONDARY)
    s["code"] = ParagraphStyle("code", fontName="Mono", fontSize=8.2, leading=11, textColor=TEXT, backColor=SURFACE,
                               borderPadding=(6, 8, 6, 8), leftIndent=8, rightIndent=8, spaceBefore=8, spaceAfter=14)
    s["toc1"] = ParagraphStyle("toc1", fontName="Body-Bold", fontSize=11, leading=20, leftIndent=0)
    s["toc2"] = ParagraphStyle("toc2", fontName="Body", fontSize=10, leading=16, leftIndent=16, textColor=SECONDARY)
    s["note"] = ParagraphStyle("note", parent=s["body"], fontSize=9.5, leading=13.5, backColor=SURFACE, borderPadding=(7, 9, 7, 9),
                               leftIndent=9, rightIndent=9, spaceBefore=6, spaceAfter=12)
    return s


S: dict[str, ParagraphStyle] = {}


# ---------------------------------------------------------------- document

class ManualDoc(BaseDocTemplate):
    """Numbers headings into the table of contents and gives every page a header and footer."""

    def __init__(self, path: str, version: str):
        super().__init__(path, pagesize=A4, leftMargin=MARGIN, rightMargin=MARGIN, topMargin=MARGIN + 6 * mm,
                         bottomMargin=MARGIN, title="SysStateTracer — User manual", author="SysStateTracer",
                         subject="User manual", creator="tools/manual/make_manual.py")
        self.version = version
        frame = Frame(MARGIN, MARGIN, CONTENT_W, PAGE_H - 2 * MARGIN - 6 * mm, id="body", leftPadding=0, rightPadding=0,
                      topPadding=0, bottomPadding=0)
        self.addPageTemplates([
            PageTemplate("title", frames=[frame], onPage=self._title_page),
            PageTemplate("content", frames=[frame], onPage=self._content_page),
        ])

    def afterFlowable(self, flowable):
        if isinstance(flowable, Paragraph) and flowable.style.name in ("h1", "h2"):
            level = 0 if flowable.style.name == "h1" else 1
            text = flowable.getPlainText()
            key = "h%d-%d" % (level, id(flowable))
            self.canv.bookmarkPage(key)
            self.canv.addOutlineEntry(text, key, level=level, closed=level > 0)
            self.notify("TOCEntry", (level, text, self.page, key))

    def _title_page(self, canvas, doc):
        canvas.saveState()
        canvas.setFillColor(ACCENT)
        canvas.rect(0, PAGE_H - 9 * mm, PAGE_W, 9 * mm, stroke=0, fill=1)
        canvas.setFillColor(SECONDARY)
        canvas.setFont("Body", 8.5)
        canvas.drawString(MARGIN, MARGIN - 8 * mm, GITHUB)
        canvas.drawRightString(PAGE_W - MARGIN, MARGIN - 8 * mm, f"Version {self.version}")
        canvas.restoreState()

    def _content_page(self, canvas, doc):
        canvas.saveState()
        canvas.setStrokeColor(RULE)
        canvas.setLineWidth(0.6)
        top = PAGE_H - MARGIN + 2 * mm
        canvas.line(MARGIN, top, PAGE_W - MARGIN, top)
        canvas.setFont("Body", 8.5)
        canvas.setFillColor(SECONDARY)
        canvas.drawString(MARGIN, top + 2.5 * mm, "SysStateTracer — User manual")
        canvas.drawRightString(PAGE_W - MARGIN, top + 2.5 * mm, f"Version {self.version}")
        canvas.drawRightString(PAGE_W - MARGIN, MARGIN - 9 * mm, str(doc.page))
        canvas.restoreState()


# ---------------------------------------------------------------- building blocks

def p(text: str, style: str = "body") -> Paragraph:
    return Paragraph(text, S[style])


def bullets(items: list[str]) -> list[Paragraph]:
    return [Paragraph(item, S["bullet"], bulletText="•") for item in items]


def table(rows: list[list[str]], widths: list[float], header: bool = True) -> Table:
    data = [[Paragraph(cell, S["cellhead" if header and i == 0 else "cell"]) for cell in row] for i, row in enumerate(rows)]
    t = Table(data, colWidths=widths, repeatRows=1 if header else 0, hAlign="LEFT")
    style = [
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("LINEBELOW", (0, 0), (-1, -1), 0.5, RULE),
        ("TOPPADDING", (0, 0), (-1, -1), 4),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 5),
        ("LEFTPADDING", (0, 0), (-1, -1), 4),
        ("RIGHTPADDING", (0, 0), (-1, -1), 6),
    ]
    if header:
        style += [("LINEBELOW", (0, 0), (-1, 0), 0.9, SECONDARY)]
    t.setStyle(TableStyle(style))
    return t


def wrap_code(text: str, width: int = 98) -> str:
    """Breaks long lines (the command-line help is up to 140 characters wide), continuing them indented."""
    out: list[str] = []
    for line in text.rstrip("\n").split("\n"):
        indent = len(line) - len(line.lstrip(" "))
        while len(line) > width:
            cut = line.rfind(" ", indent + 10, width)
            if cut <= indent:
                cut = width
            out.append(line[:cut].rstrip())
            line = " " * (indent + 4) + line[cut:].lstrip()
        out.append(line)
    return "\n".join(out)


def code(text: str) -> Preformatted:
    """A grey block in the monospaced font; it continues on the next page when it is long."""
    return Preformatted(wrap_code(text, 94), S["code"])


class Screens:
    def __init__(self, folder: Path):
        self.folder = folder
        self.missing: list[str] = []

    def figure(self, name: str, caption: str, width: float = CONTENT_W, max_height: float = 120 * mm, keep: bool = True,
               path: Path | None = None) -> list:
        """A screenshot from the CI folder, or <paramref>path</paramref> for a picture kept in the repository."""
        path = path or self.folder / f"{name}.png"
        if not path.exists():
            self.missing.append(name)
            return [p(f"<i>(Screenshot {name} is not available in this build.)</i>", "small")]
        with PilImage.open(path) as img:
            w, h = img.size
        scale = min(width / w, max_height / h)
        image = Image(str(path), width=w * scale, height=h * scale)
        frame = Table([[image]], colWidths=[w * scale + 2], hAlign="CENTER")
        frame.setStyle(TableStyle([("BOX", (0, 0), (-1, -1), 0.6, RULE), ("LEFTPADDING", (0, 0), (-1, -1), 1),
                                   ("RIGHTPADDING", (0, 0), (-1, -1), 1), ("TOPPADDING", (0, 0), (-1, -1), 1),
                                   ("BOTTOMPADDING", (0, 0), (-1, -1), 1)]))
        return [KeepTogether([frame, p(caption, "caption")])] if keep else [frame, p(caption, "caption")]


def app_icon(folder: Path, size: float) -> Image | None:
    ico = folder / "SysStateTracer.ico"
    if not ico.exists():
        return None
    png = folder / "_icon.png"
    with PilImage.open(ico) as img:
        sizes = sorted(img.info.get("sizes", [img.size]), key=lambda s: s[0])
        img.size = sizes[-1]
        img.convert("RGBA").save(png)
    return Image(str(png), width=size, height=size)


# ---------------------------------------------------------------- content

def help_parts(help_text: str) -> tuple[str, str]:
    """The /? text up to the examples, and the examples block."""
    lines = help_text.replace("\r\n", "\n").split("\n")
    try:
        start = next(i for i, l in enumerate(lines) if l.startswith("Examples:"))
    except StopIteration:
        return help_text, ""
    end = next((i for i in range(start + 1, len(lines)) if lines[i] and not lines[i].startswith(" ")), len(lines))
    examples = "\n".join(l[2:] if l.startswith("  ") else l for l in lines[start + 1:end])
    return "\n".join(lines[:start]), examples


def build(screens: Screens, help_text: str, version: str, icon_folder: Path) -> list:
    story: list = []
    today = dt.date.today().strftime("%d %B %Y").lstrip("0")

    # ---- title page
    story.append(Spacer(1, 48 * mm))
    icon = app_icon(icon_folder, 30 * mm)
    if icon is not None:
        icon.hAlign = "LEFT"
        story += [icon, Spacer(1, 10 * mm)]
    story.append(Paragraph("SysStateTracer", ParagraphStyle("t", fontName="Heading", fontSize=38, leading=44, textColor=TEXT)))
    story.append(Spacer(1, 3 * mm))
    story.append(Paragraph("User manual", ParagraphStyle("t2", fontName="Light", fontSize=22, leading=28, textColor=ACCENT)))
    story.append(Spacer(1, 12 * mm))
    story.append(Paragraph(
        "Snapshots of folders, files, the registry and Windows services — and exactly what changed between two of them. "
        "A portable tool for Windows 10 and 11: one file, nothing to install.",
        ParagraphStyle("t3", fontName="Body", fontSize=12.5, leading=19, textColor=SECONDARY)))
    story.append(Spacer(1, 18 * mm))
    story.append(p(f"Version {version} &nbsp;·&nbsp; {today}", "small"))
    story.append(NextPageTemplate("content"))
    story.append(PageBreak())

    # ---- contents
    story.append(Paragraph("Contents", ParagraphStyle("tocTitle", parent=S["h1"])))
    toc = TableOfContents()
    toc.levelStyles = [S["toc1"], S["toc2"]]
    toc.dotsMinLevel = 0
    story += [toc, PageBreak()]

    # ---- 1 Introduction
    story.append(p("1&nbsp;&nbsp;Introduction", "h1"))
    story.append(p(
        "SysStateTracer records the state of a Windows PC — folders and files, the registry and services — in a "
        "<b>snapshot</b>. Take one snapshot before a change and one after it, and SysStateTracer shows exactly what "
        "the change did: which files and folders appeared, changed or disappeared, which registry keys and values "
        "were added, modified or removed, and which services changed. Typical uses: finding out what an installer or "
        "an update really changed, tracing a setting, cleaning up after an uninstall, or comparing two PCs."))
    story.append(p("What it records", "h2"))
    story += bullets([
        "<b>Folders and files</b>: names, sizes, dates and attributes. File contents are never read.",
        "<b>Registry</b>: HKEY_LOCAL_MACHINE and every user under HKEY_USERS (your own user is HKEY_CURRENT_USER). "
        "Values up to 1 MB are stored; larger ones by size and checksum.",
        "<b>Services</b>: their state and startup type; kernel drivers on request.",
        "Anything Windows does not let it read is skipped and listed in the snapshot's properties.",
    ])
    story.append(p(
        "<b>Snapshots contain registry data, which can include sensitive settings such as product keys.</b> Treat "
        "snapshot files like a registry backup and do not share them publicly. HKLM\\SAM and HKLM\\SECURITY "
        "(password hashes and system secrets) are left out by default.", "note"))
    story.append(p("Getting SysStateTracer", "h2"))
    story += bullets([
        f'Download the latest <b>SysStateTracer.exe</b> directly from <a href="{LATEST}/SysStateTracer.exe" color="#005FB8">'
        f'{LATEST}/SysStateTracer.exe</a> (older versions: <a href="{RELEASES}" color="#005FB8">{RELEASES}</a>). It is a single '
        "file for Windows 10 and 11 x64; nothing needs to be installed. Put it in any folder you can write to.",
        "It starts without a UAC prompt, with your own rights, and also works for a standard user. Without "
        "administrator rights, snapshots miss the folders and registry keys only an administrator can read (New Snapshot "
        "shows them greyed out) and whole drives are read more slowly. For complete snapshots, click <b>Restart as "
        "administrator</b> in the main window.",
        "The program is not code-signed, so Windows may block it or SmartScreen may say “Windows protected your "
        "PC”. Before the first start, right-click the downloaded <b>SysStateTracer.exe</b>, choose <b>Properties</b>, "
        "tick <b>Unblock</b> at the bottom of the General tab and click <b>OK</b> (shown below). Each release lists the "
        "file's SHA-256 checksum, so you can check that the file is the published one.",
    ])
    story += screens.figure("unblock", "The downloaded file's Properties: (1) tick Unblock, (2) click OK.", width=70 * mm,
                            max_height=105 * mm, path=REPO / "docs" / "images" / "unblock.png")
    story += bullets([
        "SysStateTracer keeps its files beside the exe: settings, the Global Ignore list, a cache that makes the "
        "next snapshot faster, a log, and the Files and Backups folders.",
    ])
    story.append(p("Links", "h2"))
    story.append(table([
        ["", ""],
        ["Project, source code and documentation", f'<a href="{GITHUB}" color="#005FB8">{GITHUB}</a>'],
        ["Latest SysStateTracer.exe", f'<a href="{LATEST}/SysStateTracer.exe" color="#005FB8">{LATEST}/SysStateTracer.exe</a>'],
        ["Latest manual", f'<a href="{LATEST}/Manual.pdf" color="#005FB8">{LATEST}/Manual.pdf</a>'],
        ["All versions", f'<a href="{RELEASES}" color="#005FB8">{RELEASES}</a>'],
        ["Problem reports, questions and feature requests", f'<a href="{ISSUES}" color="#005FB8">{ISSUES}</a>'],
    ], [60 * mm, CONTENT_W - 60 * mm], header=False))
    story.append(PageBreak())

    # ---- 2 Using the windows
    story.append(p("2&nbsp;&nbsp;Common usage", "h1"))
    story.append(p(
        "A typical session: take a snapshot, make the change you want to trace, take a second snapshot, and compare "
        "the two. Everything here can also be done from the command line (chapter 3)."))
    story.append(p("2.1&nbsp;&nbsp;The main window", "h2"))
    story += screens.figure("w1-main-light", "The main window: the snapshots in the snapshot folder, newest first.")
    story += bullets([
        "The sidebar: <b>+</b> New Snapshot, <b>Delete</b>, <b>Preferences</b>, <b>Help</b>; Exit at the bottom. Hover over an icon to see its name.",
        "The list shows each snapshot's name, when it was taken, on which computer, how long it took and its file size. "
        "Click a column header to sort by it, and again to reverse the order; newest first is the default. "
        "Snapshots copied into the folder appear at once. Every window opens at the size and place it was last closed.",
        "Right-click a snapshot for <b>Compare with</b>, <b>Rename</b>, <b>Delete</b> and <b>Properties</b>. "
        "Keys: Ctrl+N new snapshot, Del delete, F2 rename, Enter properties, F5 refresh, Ctrl+A select all, F1 help.",
        "A file that cannot be read (damaged, cut off while copying, or made by a newer version) is listed as "
        "“Cannot be read”, so it can be deleted.",
    ])

    story.append(CondPageBreak(90 * mm))
    story.append(p("2.2&nbsp;&nbsp;Taking a snapshot", "h2"))
    story.append(p(
        "Click <b>+</b>. The name is filled in as date, time and computer name; change it to something meaningful, "
        "such as “Before Office 2024 install”. Choose what to include, then click <b>Start</b>."))
    story += screens.figure("w3-new-snapshot-light", "New Snapshot: drives, the registry and services, with the saved choice ticked.")
    story += bullets([
        "Tick or untick drives, registry hives and services. Expand a drive or a key to leave out single folders or keys. "
        "Your choice is remembered, and the command line uses it too.",
        "Items in the Global Ignore list are unticked and marked “(ignored)” (chapter 4).",
        "<b>Full rescan</b> reads everything again. Normally a snapshot after the first one reads only what changed "
        "since the previous one, using the NTFS change journal, which takes seconds instead of minutes.",
    ])
    story += screens.figure("w3-new-snapshot-running-light", "While a snapshot is being taken: progress, elapsed time, the current place and items per second.")
    story.append(p(
        "<b>Stop</b> ends it without saving anything. When the snapshot is done, the summary shows how many items it "
        "holds and how long it took, and the new snapshot is selected in the main window."))

    story.append(CondPageBreak(110 * mm))
    story.append(p("2.3&nbsp;&nbsp;Comparing two snapshots", "h2"))
    story.append(p(
        "Drag one snapshot onto the other in the main window, or right-click one and choose <b>Compare with</b>. "
        "The older snapshot is always shown on the left."))
    story += screens.figure("w4-comparison-light", "The comparison window: folders on the left, the selected folder's files on the right, the selected file in both snapshots below.")
    story.append(table([
        ["Mark", "Colour", "Meaning"],
        ["<b>+</b>", f'<font color="{NEW.hexval()}">green</font>', "New: only in the second snapshot"],
        ["<b>~</b>", f'<font color="{MODIFIED.hexval()}">orange</font>', "Modified: size, date or attributes; a registry value's type or data; a service's state or startup type"],
        ["<b>−</b>", f'<font color="{DELETED.hexval()}">red</font>', "Deleted: only in the first snapshot"],
        ["", f'<font color="{INSIDE.hexval()}">blue</font>', "A folder or key that is the same itself but holds changes"],
    ], [16 * mm, 22 * mm, CONTENT_W - 38 * mm]))
    story.append(Spacer(1, 6))
    story += bullets([
        "<b>Left</b>: File system, Registry and Services, with how much changed in each, then drives, folders, hives "
        "and keys, joined by dotted lines; click <b>+</b> to open one. A folder or key shows how many changes are inside "
        "it; a new or deleted folder shows how many items it held. The legend at the bottom explains the colours.",
        "<b>Show all</b> also lists folders, files, keys and values that are the same in both snapshots, in normal "
        "text; <b>Show only changes</b> lists only what changed and the folders and keys leading to it. The choice is "
        "remembered.",
        "<b>Right</b>: the files of the selected folder, the values of the selected key, or every service. Click a column "
        "heading to sort by it, again to turn the order round. Values that differ are highlighted. <b>Include "
        "subfolders</b> also lists the files of every folder below, named from the selected one (<i>root\\office00.dll</i>).",
        "<b>Below</b>: the selected item in both snapshots, side by side — size, date and attributes; type and data; "
        "state and startup type. Its full path is in the status bar.",
        "A new or deleted folder or key can be opened like any other: what it holds comes from the snapshot that has "
        "it. Those items are for reading and Jump to; the other actions work on the folder or key itself.",
        "<b>Swap</b> exchanges the two sides: new and deleted trade places. <b>Refresh</b> compares again, for "
        "example after the Global Ignore list was changed. Both keep the open folders and the selection.",
        "Select several items with Ctrl+click and Shift+click, then right-click for the actions below.",
    ])
    story += screens.figure("w4-comparison-subfolders-light", "Include subfolders: every file in a new folder, named from the selected folder.", width=150 * mm)
    story.append(CondPageBreak(75 * mm))
    story.append(p("Actions (right-click)", "h3"))
    story.append(table([
        ["Action", "For", "What it does"],
        ["Jump to", "one item", "Opens Explorer with the file selected, Regedit at the key, or the Services console at the service"],
        ["Mark as non-important", "any items", "Strikes them through until the window is closed; they are left out of exports"],
        ["Add to Global Ignore list", "any items", "Adds rules for them to the local Global Ignore list and hides them (chapter 4)"],
        ["Export to .REG", "registry items, a whole hive or the Registry part", "Writes the new and modified keys and values, as the newer snapshot has them, to a .reg file that Regedit can import"],
        ["Copy to .\\Files", "folders and files, a whole drive or the File system part", "Copies the new and modified files from the disk into the Files folder, keeping their folders"],
        ["Revert Value", "registry keys and values", "Puts the values back as the older snapshot has them, after saving a backup"],
    ], [38 * mm, 42 * mm, CONTENT_W - 80 * mm]))

    story.append(CondPageBreak(100 * mm))
    story.append(p("2.4&nbsp;&nbsp;Reverting registry values", "h2"))
    story.append(p(
        "<b>Revert Value</b> always goes back to the snapshot that was taken first, whichever side it is shown on. "
        "It lists every value as it is now and as it will be, and asks first:"))
    story += screens.figure("dialog-revert-light", "Revert Value: the current and the restored value of each registry value.", width=125 * mm)
    story.append(p(
        "A modified value gets its old type and data back, a new value is deleted, a deleted value is created again. "
        "Before anything changes, the current values are saved to a .reg file in the Backups folder; importing that "
        "file (double-click it) undoes the revert."))

    story.append(CondPageBreak(90 * mm))
    story.append(p("2.5&nbsp;&nbsp;Copying files to .\\Files", "h2"))
    story += screens.figure("dialog-copy-to-files-light", "Copy to .\\Files: progress, and the files that could not be copied.", width=125 * mm)
    story += bullets([
        "Files keep their folders: C:\\ProgramData\\App\\x.ini becomes Files\\ProgramData\\App\\x.ini; files from other "
        "drives go under _D, _E and so on (D:\\Data\\x.xlsx becomes Files\\_D\\Data\\x.xlsx).",
        "Files that are locked, gone or not readable are skipped and listed.",
        "<b>Restore-Files.ps1</b> in the Files folder copies everything back to where it came from. Try it first with "
        "<font face=\"Mono\">.\\Restore-Files.ps1 -WhatIf</font>, which only lists what it would copy.",
    ])

    story.append(CondPageBreak(90 * mm))
    story.append(p("2.6&nbsp;&nbsp;Snapshot properties", "h2"))
    story += screens.figure("w2-properties-light", "Snapshot Properties: when, where and how the snapshot was taken.", width=120 * mm)
    story.append(p(
        "Properties show the snapshot's file, when and on which computer it was taken, how long each part took, whether "
        "it was a full or an incremental snapshot (and why), what it includes, how many items it holds, and every item "
        "that could not be read, with the reason."))
    story.append(PageBreak())

    # ---- 3 Command line
    head, examples = help_parts(help_text)
    story.append(p("3&nbsp;&nbsp;Command-line usage", "h1"))
    story.append(p(
        "Every task can be run from a command prompt, a script or a scheduled task. With a switch, SysStateTracer runs "
        "that one task and exits; without one, the main window opens. It runs with the rights of the console that "
        "starts it: from a console opened with <b>Run as administrator</b> snapshots are complete; from a normal console, "
        "what needs administrator rights is skipped and a note says so. Everything it prints is also written to "
        "SysStateTracer.log beside the exe."))
    story.append(p("Switches", "h2"))
    story.append(table([
        ["Switch", "What it does"],
        ["<font face=\"Mono\">/?</font>", "Shows the help: every switch, the folders in use, exit codes and examples."],
        ["<font face=\"Mono\">/new-snap [\"name\"]</font>", "Takes a snapshot with the choice saved in the New Snapshot window. Without a name, it is named by date, time and computer."],
        ["<font face=\"Mono\">/delete-snap [\"name\"]</font>", "Deletes the snapshot; without a name, the newest one. Prints which one it deleted."],
        ["<font face=\"Mono\">/rename-snap \"old\" \"new\"</font>", "Renames a snapshot. A path as the new name moves it to that folder."],
        ["<font face=\"Mono\">/compare-snap [\"name1\" \"name2\"] [\"report.html\"]</font>", "Compares two snapshots (the newest two without names) and writes a self-contained HTML report (results.html by default)."],
        ["<font face=\"Mono\">/export [\"name1\" \"name2\"]</font>", "Writes the new and modified registry keys and values to Export_&lt;name1&gt;_vs_&lt;name2&gt;.reg and copies the new and modified files to the Files folder, with Restore-Files.ps1."],
    ], [62 * mm, CONTENT_W - 62 * mm]))
    story.append(Spacer(1, 6))
    story += bullets([
        "A snapshot is named by its file name, with or without .stsnap, in the snapshot folder — or by the path of a "
        ".stsnap file anywhere, such as <font face=\"Mono\">\"D:\\Snapshots\\Before patch.stsnap\"</font>. Put names "
        "and paths with spaces in quotes.",
        "Comparisons and exports leave out everything the Global Ignore lists set in Preferences match.",
        "A window program does not hold up an interactive command prompt, so the prompt may come back before the "
        "output. In batch files use <font face=\"Mono\">start \"\" /wait SysStateTracer.exe …</font>; in PowerShell "
        "<font face=\"Mono\">Start-Process … -Wait</font>.",
    ])
    story.append(p("Exit codes", "h2"))
    story.append(table([
        ["Code", "Meaning"],
        ["0", "Success"],
        ["1", "Wrong arguments: unknown switch, wrong number of names, a name that cannot be used or is taken"],
        ["2", "Snapshot not found, or fewer than two snapshots for /compare-snap and /export"],
        ["4", "A file could not be read or written (also a damaged or newer-format snapshot)"],
        ["5", "Cancelled with Ctrl+C"],
    ], [18 * mm, CONTENT_W - 18 * mm]))
    if examples:
        story.append(CondPageBreak(60 * mm))
        story.append(p("Examples", "h2"))
        story.append(code(examples))
    story.append(CondPageBreak(80 * mm))
    story.append(p("The complete help (SysStateTracer.exe /?)", "h2"))
    story.append(code(head))
    story.append(PageBreak())

    # ---- 4 Global Ignore list
    story.append(p("4&nbsp;&nbsp;Editing the Global Ignore list", "h1"))
    story.append(p(
        "Some places change all the time without anything being installed: temporary folders, caches, logs, usage "
        "counters in the registry. The <b>Global Ignore list</b> leaves them out, so comparisons show what matters. "
        "Rules are applied when a snapshot is taken (the items are not read at all) and again when snapshots are "
        "compared and exported. SysStateTracer comes with rules for the usual noise; every rule can be removed."))
    story.append(p("4.1&nbsp;&nbsp;Which lists are used: local, and remote", "h2"))
    story += screens.figure("w5-preferences-ignore-list-light", "Preferences → Global Ignore list.", max_height=105 * mm)
    story += bullets([
        "The <b>local list</b>, SysStateTracer.ignore.txt beside the program, is always used. Edit it here (or in any "
        "text editor); rules added from the comparison window go to it.",
        "<b>Also use the shared remote list</b> (ticked by default) adds the rules of a list read from a web address or "
        "a file, also on a network share such as \\\\server\\share\\SysStateTracer.ignore.txt, so several PCs can share "
        "one list. The default is the shared list in the project on GitHub. <b>Check now</b> reads it and shows how many "
        "rules it has. If it cannot be read, the local list is used alone.",
    ])
    story.append(p("4.2&nbsp;&nbsp;Adding and removing rules", "h2"))
    story.append(p(
        "Below is the local list. <b>Add rule</b> lets you type rules, with wildcards too; <b>Edit</b> (or a double-click, "
        "F2) changes the selected rule. <b>Add folder</b> picks a folder; <b>Add registry key</b> asks for a key "
        "path; <b>Add service</b> lists the installed services. <b>Remove</b> deletes the selected rules (also with Del). "
        "<b>Import</b> adds the rules from a text file; <b>Export</b> saves the list. Click <b>OK</b> to save."))
    figures = screens.figure("dialog-add-registry-key-light", "Add registry key.", width=82 * mm, max_height=45 * mm, keep=False)
    figures2 = screens.figure("dialog-add-service-light", "Add service.", width=82 * mm, max_height=75 * mm, keep=False)
    side = Table([[figures, figures2]], colWidths=[CONTENT_W / 2, CONTENT_W / 2])
    side.setStyle(TableStyle([("VALIGN", (0, 0), (-1, -1), "TOP"), ("LEFTPADDING", (0, 0), (-1, -1), 0), ("RIGHTPADDING", (0, 0), (-1, -1), 4)]))
    story.append(side)
    story.append(p("4.3&nbsp;&nbsp;Adding rules from a comparison", "h2"))
    story.append(p(
        "In the comparison window, select items that are only noise, right-click and choose <b>Add to Global Ignore "
        "list</b>. SysStateTracer shows the rules it will add, and you can change them first — for example replace a "
        "user name with * so the rule applies to every user. It then saves them to the local list and hides the items, "
        "keeping the tree open as it was."))
    story += screens.figure("dialog-add-to-ignore-light", "Add to Global Ignore list, from the comparison window.", width=120 * mm)
    story.append(CondPageBreak(80 * mm))
    story.append(p("4.4&nbsp;&nbsp;Rule syntax", "h2"))
    story.append(p("One rule per line; blank lines and lines starting with # are ignored. Upper and lower case do not matter."))
    story.append(table([
        ["Rule", "Leaves out", "Example"],
        ["FS:", "a folder or file, and everything in it", "<font face=\"Mono\">FS:C:\\Windows\\Temp</font>"],
        ["REG:", "a registry key and everything below it", "<font face=\"Mono\">REG:HKLM\\SOFTWARE\\Vendor\\Telemetry</font>"],
        ["REG:", "a single registry value: key path, \\, value name", "<font face=\"Mono\">REG:HKLM\\SOFTWARE\\Vendor\\LastRun</font>"],
        ["SVC:", "a service, by its service name (Properties → Service name in services.msc)", "<font face=\"Mono\">SVC:edgeupdate</font>"],
    ], [16 * mm, 70 * mm, CONTENT_W - 86 * mm]))
    story.append(Spacer(1, 6))
    story += bullets([
        "<font face=\"Mono\">*</font> matches any text, including \\: <font face=\"Mono\">FS:*\\AppData\\Local\\Temp</font> "
        "matches the Temp folder of every user. <font face=\"Mono\">?</font> matches one character.",
        "HKCU means every user (HKU\\*); HKCR and HKCC are the HKLM keys they show. CurrentControlSet matches the real "
        "ControlSet001, ControlSet002… keys.",
    ])
    story.append(PageBreak())

    # ---- 5 Preferences
    story.append(p("5&nbsp;&nbsp;Preferences", "h1"))
    story.append(p("Open Preferences with the gear icon in the main window. Changes are saved with <b>OK</b>; <b>Cancel</b> leaves everything as it was."))
    story += screens.figure("w5-preferences-general-light", "Preferences → General: the snapshot folder, the theme and the list options.", max_height=105 * mm)
    story.append(p("Snapshot folder", "h2"))
    story.append(p(
        "Where new snapshots are saved, and which folder the main window lists. <b>Browse</b> picks another folder; "
        "<b>Program folder</b> goes back to the folder of SysStateTracer.exe. SysStateTracer checks that it can write to "
        "the folder before saving the choice."))
    story.append(p("Theme", "h2"))
    story.append(p(
        "<b>Follow Windows</b> uses the light or dark mode set in Windows (Settings → Personalization → Colors) and "
        "switches with it; <b>Light</b> and <b>Dark</b> fix the look. Click a picture or its button: every open window "
        "changes at once."))
    story += screens.figure("w1-main-dark", "The main window in the dark theme.", width=135 * mm)
    story.append(p("Lists", "h2"))
    story.append(p(
        "<b>Show grid lines</b> draws lines between the rows and columns, and <b>Use zebra striping</b> shades every "
        "other row, in the snapshot list of the main window and in the comparison window. Both are off by default, and "
        "open windows change at once."))
    story.append(p("Global Ignore list", "h2"))
    story.append(p("The second tab turns the shared remote list on or off and edits the local list; see chapter 4."))
    story.append(PageBreak())

    # ---- 6 Feedback
    story.append(p("6&nbsp;&nbsp;Feedback, issues and feature requests", "h1"))
    story.append(p(
        "SysStateTracer is developed in the open on GitHub. Problems, questions, ideas and feature requests are "
        "welcome: report them in the <b>Issues</b> tab of the repository."))
    story.append(p(f'<a href="{ISSUES}" color="#005FB8"><b>{ISSUES}</b></a>'))
    story += bullets([
        "Click <b>New issue</b>, give it a short title, and describe the problem or the feature you would like. "
        "A free GitHub account is needed to post.",
        "Search the open and closed issues first: the problem may already be known, or fixed in a newer version.",
    ])
    story.append(p("What helps in a problem report", "h2"))
    story += bullets([
        "The version (Help / About, or the first line of <font face=\"Mono\">SysStateTracer.exe /?</font>) and the Windows version.",
        "What you did, what you expected and what happened; a screenshot of the window if it shows the problem.",
        "The lines about it from <b>SysStateTracer.log</b>, beside the exe.",
        "For a Global Ignore rule that does not work: the rule as written and the full path it should match.",
    ])
    story.append(p(
        "<b>Do not attach snapshot files publicly.</b> They contain registry data, which can include product keys "
        "and other sensitive settings; describe the items instead, or share a snapshot privately.", "note"))
    story.append(Spacer(1, 10))
    story.append(p(f'More documentation, the source code and new versions: <a href="{GITHUB}" color="#005FB8">{GITHUB}</a>', "small"))
    return story


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--screens", required=True, help="folder with the screenshots (name-light.png)")
    parser.add_argument("--help-text", dest="help_text", required=True, help="text file with the output of SysStateTracer.exe /?")
    parser.add_argument("--version", required=True)
    parser.add_argument("--icon", default=str(Path(__file__).resolve().parents[2] / "src" / "SysStateTracer"), help="folder with SysStateTracer.ico")
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    register_fonts()
    S.update(styles())
    help_text = Path(args.help_text).read_text(encoding="utf-8-sig", errors="replace")
    screens = Screens(Path(args.screens))
    doc = ManualDoc(args.out, re.sub(r"\+.*$", "", args.version))
    doc.multiBuild(build(screens, help_text, re.sub(r"\+.*$", "", args.version), Path(args.icon)))
    if screens.missing:
        print("Missing screenshots: " + ", ".join(screens.missing))
    print(f"Wrote {args.out}")


if __name__ == "__main__":
    main()
