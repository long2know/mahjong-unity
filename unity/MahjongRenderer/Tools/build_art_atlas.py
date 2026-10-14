#!/usr/bin/env python3
"""Rebuild/check the pinned CC0 artwork. Generation alone requires Pillow."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import struct
import zlib

PROJECT = Path(__file__).resolve().parents[1]
ATLAS_SIZE, CELL_WIDTH, CELL_HEIGHT = 1536, 192, 288
NAMES = ([f"Man{i}" for i in range(1, 10)] + [f"Pin{i}" for i in range(1, 10)]
         + [f"Sou{i}" for i in range(1, 10)]
         + ["Ton", "Nan", "Shaa", "Pei", "Haku", "Hatsu", "Chun",
            "Man5-Dora", "Pin5-Dora", "Sou5-Dora"])
LABELS = ([f"{i}{suit}" for suit in "MPS" for i in range(1, 10)]
          + ["E", "S", "W", "N", "WD", "GD", "RD", "5MR", "5PR", "5SR"])
PALETTES = [(35, 105, 116), (165, 113, 55)]
LABEL_PATHS = {
    "1": [[(.12,.80),(.30,1),(.30,0)], [(.10,0),(.50,0)]],
    "2": [[(0,.80),(.05,.94),(.20,1),(.43,1),(.60,.86),(.60,.70),(0,0),(.60,0)]],
    "3": [[(0,1),(.46,1),(.60,.86),(.60,.66),(.40,.52),(.20,.52)],
          [(.40,.52),(.60,.38),(.60,.16),(.46,0),(0,0)]],
    "4": [[(.45,0),(.45,1),(0,.32),(.60,.32)]],
    "5": [[(.60,1),(0,1),(0,.56),(.40,.56),(.60,.40),(.60,.16),(.44,0),(0,0)]],
    "6": [[(.54,1),(.22,1),(0,.72),(0,.16),(.16,0),(.44,0),(.60,.16),
           (.60,.40),(.44,.56),(0,.56)]],
    "7": [[(0,1),(.60,1),(.12,0)]],
    "8": [[(.16,.52),(0,.68),(0,.84),(.16,1),(.44,1),(.60,.84),(.60,.68),
           (.44,.52),(.16,.52),(0,.36),(0,.16),(.16,0),(.44,0),(.60,.16),
           (.60,.36),(.44,.52)]],
    "9": [[(.60,.44),(.16,.44),(0,.60),(0,.84),(.16,1),(.44,1),(.60,.84),
           (.60,.26),(.38,0),(.06,0)]],
    "M": [[(0,0),(0,1),(.30,.44),(.60,1),(.60,0)]],
    "P": [[(0,0),(0,1),(.44,1),(.60,.84),(.60,.66),(.44,.50),(0,.50)]],
    "S": [[(.60,.92),(.44,1),(.16,1),(0,.84),(0,.68),(.16,.54),(.44,.46),
           (.60,.32),(.60,.16),(.44,0),(.16,0),(0,.08)]],
    "E": [[(.60,1),(0,1),(0,0),(.60,0)], [(0,.50),(.48,.50)]],
    "W": [[(0,1),(.12,0),(.30,.46),(.48,0),(.60,1)]],
    "N": [[(0,0),(0,1),(.60,0),(.60,1)]],
    "D": [[(0,0),(0,1),(.30,1),(.52,.84),(.60,.66),(.60,.34),(.52,.16),(.30,0),(0,0)]],
    "G": [[(.60,.90),(.44,1),(.16,1),(0,.82),(0,.18),(.16,0),(.44,0),(.60,.16),
           (.60,.48),(.34,.48)]],
    "R": [[(0,0),(0,1),(.44,1),(.60,.84),(.60,.66),(.44,.50),(0,.50)],
          [(.30,.50),(.60,0)]],
}

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def rgba(path):
    """Decode actual 8-bit noninterlaced RGBA PNGs without a CI dependency."""
    data = path.read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("Not a real PNG: " + str(path))
    offset, compressed = 8, []
    while offset < len(data):
        length = struct.unpack_from(">I", data, offset)[0]
        kind, part = data[offset + 4:offset + 8], data[offset + 8:offset + 8 + length]
        if kind == b"IHDR":
            width, height, bits, color, _, _, interlace = struct.unpack(">IIBBBBB", part)
            if (bits, color, interlace) != (8, 6, 0):
                raise ValueError("Expected RGBA8: " + str(path))
        if kind == b"IDAT":
            compressed.append(part)
        offset += length + 12
    raw = zlib.decompress(b"".join(compressed))
    stride, previous, pixels = width * 4, bytearray(width * 4), bytearray()
    for y in range(height):
        method = raw[y * (stride + 1)]
        row = bytearray(raw[y * (stride + 1) + 1:(y + 1) * (stride + 1)])
        for x in range(stride):
            a, b, c = (row[x - 4] if x >= 4 else 0), previous[x], (previous[x - 4] if x >= 4 else 0)
            p = a + b - c
            pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
            predictor = (0, a, b, (a + b) // 2, a if pa <= pb and pa <= pc else b if pb <= pc else c)[method]
            row[x] = (row[x] + predictor) & 255
        pixels.extend(row)
        previous = row
    return width, height, pixels

def check():
    provenance = json.loads((PROJECT / "Documentation/art-provenance.json").read_text())
    imported = provenance["importedArtwork"]
    assert imported["commitSha"] == "26e127ba2117f45cdce5ea0225748cc0cfad3169"
    assert [item["basename"] for item in imported["faceMapping"]] == NAMES
    assert [item["assistLabel"] for item in imported["faceMapping"]] == LABELS
    for item in imported["originalFiles"] + provenance["runtimeTextures"]:
        path = PROJECT / item["path"]
        assert path.is_file() and path.stat().st_size == item["bytes"] and sha(path) == item["sha256"], str(path)
        if "gitBlobSha" in item:
            content = path.read_bytes()
            assert hashlib.sha1(b"blob " + str(len(content)).encode() + b"\0" + content).hexdigest() == item["gitBlobSha"]
    width, height, pixels = rgba(PROJECT / "Assets/Resources/Art/Faces.png")
    assert (width, height) == (ATLAS_SIZE, ATLAS_SIZE)
    for index in range(37):
        x, bottom = index % 8 * CELL_WIDTH, index // 8 * CELL_HEIGHT
        top = height - bottom - CELL_HEIGHT
        alpha = bytearray()
        for y in range(top, top + CELL_HEIGHT):
            alpha.extend(pixels[(y * width + x) * 4 + 3:(y * width + x + CELL_WIDTH) * 4:4])
        assert (max(alpha) == 0) == (index == 31), f"Blank/missing real face {index}: {NAMES[index]}"
        assert imported["faceMapping"][index]["atlasRectPixelsBottomLeft"] == [x, bottom, CELL_WIDTH, CELL_HEIGHT]
    source = PROJECT / "Assets/Art/FluffyStuff/Export/Regular/Haku.png"
    assert max(rgba(source)[2][3::4]) == 0, "White dragon must stay intentionally blank"
    for index in range(2):
        assert rgba(PROJECT / f"Assets/Resources/Art/Back{index}.png")[:2] == (CELL_WIDTH, CELL_HEIGHT)
    assert sha(PROJECT / "Assets/Resources/Art/Back0.png") != sha(PROJECT / "Assets/Resources/Art/Back1.png")
    print("Pinned CC0 originals, all 37 actual atlas cells, blank Haku, two backs and recorded hashes verified.")

def generate(source_root, receipt_path, table_path):
    from PIL import Image, ImageDraw
    receipt = json.loads(receipt_path.read_text())
    original = PROJECT / "Assets/Art/FluffyStuff"
    runtime = PROJECT / "Assets/Resources/Art"
    runtime.mkdir(parents=True, exist_ok=True)
    files = []
    for entry in receipt["files"]:
        source = source_root / entry["path"]
        content = source.read_bytes()
        assert len(content) == entry["bytes"] and sha(source) == entry["sha256"]
        assert hashlib.sha1(b"blob " + str(len(content)).encode() + b"\0" + content).hexdigest() == entry["gitBlobSha"]
        destination = original / entry["path"]
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, destination)
        files.append({key: entry[key] for key in ("sourceUrl", "gitBlobSha", "bytes", "sha256")}
                     | {"path": destination.relative_to(PROJECT).as_posix(), "transformation": "none; byte-identical creator file"})
    atlases = [Image.new("RGBA", (ATLAS_SIZE, ATLAS_SIZE)) for _ in range(2)]
    mapping = []
    for index, name in enumerate(NAMES):
        image = Image.open(original / "Export/Regular" / (name + ".png")).convert("RGBA")
        assert image.size == (600, 800)
        art = image.resize((168, 224), Image.Resampling.LANCZOS)
        x, bottom = index % 8 * CELL_WIDTH, index // 8 * CELL_HEIGHT
        top = ATLAS_SIZE - bottom - CELL_HEIGHT
        for atlas in atlases:
            atlas.paste(art, (x + 12, top + 20))
        label = LABELS[index]
        ink = (107, 57, 48, 255) if index >= 34 else (61, 78, 79, 255)
        scale = 4
        overlay = Image.new("RGBA", (CELL_WIDTH * scale, CELL_HEIGHT * scale))
        draw = ImageDraw.Draw(overlay)
        label_height = 28 * scale
        advance = .76 * label_height
        start = (CELL_WIDTH * scale - (len(label) - 1) * advance - .60 * label_height) / 2
        for character, value in enumerate(label):
            for stroke in LABEL_PATHS[value]:
                points = [(start + character * advance + px * label_height,
                           (CELL_HEIGHT - 7) * scale - py * label_height) for px, py in stroke]
                weight = round(label_height * .075)
                draw.line(points, fill=ink, width=weight, joint="curve")
                for px, py in points:
                    radius = weight / 2
                    draw.ellipse((px - radius, py - radius, px + radius, py + radius), fill=ink)
        overlay = overlay.resize((CELL_WIDTH, CELL_HEIGHT), Image.Resampling.LANCZOS)
        atlases[1].alpha_composite(overlay, (x, top))
        mapping.append({"faceIndex": index, "basename": name, "assistLabel": label,
                        "atlasRectPixelsBottomLeft": [x, bottom, CELL_WIDTH, CELL_HEIGHT],
                        "artRectPixelsCellTopLeft": [12, 20, 168, 224]})
    for name, image in zip(("Faces.png", "FacesLabelled.png"), atlases):
        image.save(runtime / name, compress_level=9)
    source = Image.open(original / "Export/Regular/Back.png").convert("RGBA")
    for index, palette in enumerate(PALETTES):
        tinted = Image.new("RGBA", source.size)
        tinted.putdata([(*(round(channel * (.62 + .38 * r / 255)) for channel in palette), a)
                        for r, g, b, a in source.getdata()])
        back = Image.new("RGBA", (CELL_WIDTH, CELL_HEIGHT))
        back.paste(tinted.resize((168, 224), Image.Resampling.LANCZOS), (12, 32))
        back.save(runtime / f"Back{index}.png", compress_level=9)
    assert sha(table_path) == "3028ff6dd0c1c5f16e99e22fb96790f0148c4465368c77c776529973f8874e0f"
    shutil.copyfile(table_path, runtime / "TableFabric.jpg")
    textures = [{"path": path.relative_to(PROJECT).as_posix(), "bytes": path.stat().st_size, "sha256": sha(path)}
                for path in sorted(runtime.iterdir()) if path.suffix in (".png", ".jpg")]
    provenance = {
        "assetProvenanceVersion": "1.1.0",
        "creator": "Unity specialist (GitHub Copilot), original renderer geometry and presentation for Stephen Long",
        "createdAtUtcDate": "2026-10-12",
        "licenseDisposition": "Original project contributions retain the pending owner-license disposition. Imported artwork below is separately CC0-1.0; no blanket MIT grant is implied.",
        "originalProceduralAssets": [
            "Original rounded/beveled tile, die and table meshes, unit box and horizontal quad",
            "Original point-stick values, EAST marker and anti-aliased die pip textures",
            "Original rounded-stroke vector ASCII public labels and assist-label rasterization; no imported font",
            "Original directional/specular surface shading, fabric tint/vignette, rail grain, soft contact shadows and outline cues"
        ],
        "importedArtwork": {
            "creator": "FluffyStuff", "repository": receipt["repository"],
            "commitSha": receipt["commitSha"], "treeSha": receipt["treeSha"],
            "license": "CC0-1.0", "creatorLicensePath": "Assets/Art/FluffyStuff/LICENSE.md",
            "licenseRetainedByteIdentically": True,
            "originalFiles": files, "faceMapping": mapping,
            "whiteDragon": "Index 31 / Haku is deliberately entirely transparent: an ordinary visible ivory body, never an unknown-face sentinel.",
            "transformations": {
                "tool": "Tools/build_art_atlas.py", "generationDependency": "Pillow (offline only; runtime and validation need no image/font package)",
                "atlases": {"width": ATLAS_SIZE, "height": ATLAS_SIZE, "columns": 8, "rows": 5,
                            "cellWidth": CELL_WIDTH, "cellHeight": CELL_HEIGHT, "pixelFormat": "RGBA8",
                            "packing": "Source full 600x800 resampled to 168x224 with Lanczos, preserving 3:4 artwork inside native 2:3 cells. Cell 0 is at the PNG bottom left; glyph top points to +Z on the face quad. Transparent gutters remain; the material composites on matching ivory.",
                            "uv": "Half-texel inset of each full cell. Padding, trilinear mipmaps and clamped edges avoid adjacent glyph bleed at playable scale.",
                            "assistLabels": "Second atlas adds small original antialiased rounded-stroke labels in the lower letterbox, not a replacement glyph or an OS font."},
                "backs": {"source": "Export/Regular/Back.png", "backIndexes": [0, 1],
                          "recolorFormula": "RGB channel = palette channel * (0.62 + 0.38 * source red / 255); preserve source alpha, rounded panel and shading; then Lanczos to168x224 inside192x288.",
                          "palettesSrgb": PALETTES, "identityBoundary": "Back styles stay separate from all 37 face indices; no per-face rear patterns."}
            }
        },
        "importedTable": {
            "path": "Assets/Resources/Art/TableFabric.jpg", "creator": "ambientCG (formerly CC0 Textures)",
            "asset": "Fabric030", "license": "CC0-1.0",
            "primaryAssetUrl": "https://ambientcg.com/view?id=Fabric030",
            "primaryLicenseUrl": "https://docs.ambientcg.com/license/",
            "forkRepository": "long2know/mahjong-autotable",
            "forkCommitSha": "d10d351e543aa3e341960de9800828b6c0662a82",
            "gitBlobSha": "f801a7ebe1431cd954a9963b180869eba3557bc3",
            "sha256": sha(table_path), "bytes": table_path.stat().st_size,
            "transformation": "Byte-identical existing fork JPEG; material-only neutral-luminance teal tint and tiled UVs. Earlier ambientCG-to-fork processing is unspecified; this is not a fresh PBR bundle."
        },
        "runtimeTextures": textures,
        "runtimeBudget": {"importedTextureCount": 5, "maximumUncompressedRgbaMipBytes": 28000000,
                          "cpuReadability": False, "compression": "Unity normal-quality automatic compression, with a bounded RGBA fallback on unsupported hardware.",
                          "sourceOriginals": "The 38 PNGs and SVG masters are provenance inputs outside Resources; never loaded individually in the player.",
                          "rendering": "Built-in pipeline, 4x MSAA requested, no HDR/postprocessing/URP or realtime shadow maps. Shared meshes/material, original analytic contact shadows."},
        "excluded": ["Kanojo/NC tile art or derivatives", "legacy GLB/Blender geometry of unverified rights",
                     "Majsoul, sample FBX/scenes, Photon, DOTween, third-party fonts, sounds and SDKs"],
        "engineComponents": "Unity runtime/compiler, UnityCG include, Newtonsoft and Test Framework retain their own terms."
    }
    (PROJECT / "Documentation/art-provenance.json").write_text(json.dumps(provenance, indent=2) + "\n")
    check()

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-root", type=Path)
    parser.add_argument("--receipt", type=Path)
    parser.add_argument("--table", type=Path)
    args = parser.parse_args()
    if args.source_root:
        if not args.receipt or not args.table:
            parser.error("Generation requires the audited source root, receipt and selected table.")
        generate(args.source_root, args.receipt, args.table)
    else:
        check()
