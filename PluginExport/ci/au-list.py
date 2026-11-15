#!/usr/bin/env python3
"""Prints "<AU type>|<AU subtype>" for every plug-in (PluginExport/plugins.json), for the auval step."""
import json, os
for p in json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "plugins.json"))):
    print(("aumu" if p["kind"] == "instrument" else "aufx") + "|" + p["au"])
