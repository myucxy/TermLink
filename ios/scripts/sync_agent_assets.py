#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-only
"""Convert the already licensed Android agent assets; never download artwork."""
import json
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET
from xml.sax.saxutils import quoteattr

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "android/app/src/main/res"
TARGET = ROOT / "ios/TermLink/Resources/Assets.xcassets"
ANDROID = "{http://schemas.android.com/apk/res/android}"


def attribute(node, name, default=""):
    return node.get(ANDROID + name, default)


def geometry(node):
    if node.tag == "path":
        fill = attribute(node, "fillColor", "#000000")
        rule = "evenodd" if attribute(node, "fillType") == "evenOdd" else "nonzero"
        return f'<path fill={quoteattr(fill)} fill-rule="{rule}" d={quoteattr(attribute(node, "pathData"))}/>'
    if node.tag == "group":
        x, y = attribute(node, "translateX", "0"), attribute(node, "translateY", "0")
        sx, sy = attribute(node, "scaleX", "1"), attribute(node, "scaleY", "1")
        return f'<g transform="translate({x} {y}) scale({sx} {sy})">' + "".join(map(geometry, node)) + "</g>"
    raise ValueError(f"Unsupported VectorDrawable element: {node.tag}")


def sync():
    TARGET.mkdir(parents=True, exist_ok=True)
    (TARGET / "Contents.json").write_text(json.dumps({"info": {"author": "xcode", "version": 1}}, indent=2) + "\n", encoding="utf-8")
    for brand in ["openai", "claude", "opencode", "grok", "zcode"]:
        name = "ai_agent_" + brand
        directory = TARGET / (name + ".imageset")
        directory.mkdir(exist_ok=True)
        extension = "png" if brand == "zcode" else "svg"
        filename = name + "." + extension
        if extension == "png":
            shutil.copyfile(SOURCE / "drawable-nodpi" / filename, directory / filename)
        else:
            vector = ET.parse(SOURCE / "drawable" / (name + ".xml")).getroot()
            width, height = attribute(vector, "viewportWidth"), attribute(vector, "viewportHeight")
            svg = f'<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 {width} {height}">' + "".join(map(geometry, vector)) + "</svg>\n"
            (directory / filename).write_text(svg, encoding="utf-8")
        properties = {"preserves-vector-representation": True} if extension == "svg" else {}
        if brand in ["openai", "opencode", "grok"]:
            properties["template-rendering-intent"] = "template"
        contents = {"images": [{"filename": filename, "idiom": "universal"}], "info": {"author": "xcode", "version": 1}, "properties": properties}
        (directory / "Contents.json").write_text(json.dumps(contents, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    sync()
