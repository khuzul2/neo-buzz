#!/usr/bin/env python3
"""Regenerates the Tutorial Tremolo fixture's generated files (docs/sdk/tutorial.md): Engine/Params.g.cs,
Machine.Params.g.cs, "Tutorial Tremolo.machine.json" and "Tutorial Tremolo.prs.xml".

    python3 NeoBuzz.MachineTests/Sdk/TutorialTremolo/regenerate.py

A real machine's folder is named by its Studio and Portable projects and its entry is in Machines/Manifests/machine-catalog.json,
and tools/gen-machine-params.py reads both. This fixture has neither (it is not shipped), so this script runs the same generators
and gives them the gear name and the catalogue entry that Entries.txt holds (the snippets TutorialGear and Catalog).
"""
import importlib.util
import json
import os
import re

here = os.path.dirname(os.path.abspath(__file__))
tools = os.path.normpath(os.path.join(here, '..', '..', '..', 'tools'))


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, os.path.join(tools, file))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def snippet(name):
    """The lines between '# snippet Name' and '# end snippet Name' of Entries.txt."""
    lines = open(os.path.join(here, 'Entries.txt'), encoding='utf-8').read().split('\n')
    start = lines.index(f'# snippet {name}') + 1
    end = lines.index(f'# end snippet {name}')
    return '\n'.join(lines[start:end])


GEAR = 'Tutorial Tremolo'
entry = json.loads(snippet('Catalog'))

params = load('genparams', 'gen-machine-params.py')
params.gear_of = lambda folder: (GEAR, 'effect')
real_fields = params.mc.fields
params.mc.fields = lambda gear: dict(entry) if gear == GEAR else real_fields(gear)
params.generate(here)

presets = load('genpresets', 'gen-machine-presets.py')
presets.build(here)
