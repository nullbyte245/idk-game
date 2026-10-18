"""Prints the arms rig's real bone hierarchy: the finger chains have to hang off the hand bone for a
curl to propagate, and this rig came out of a Source export where that is not guaranteed."""
import os, sys
import bpy

def arg(name):
    return sys.argv[sys.argv.index(name) + 1]

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=os.path.abspath(arg('--arms')))
arm = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
for b in arm.data.bones:
    print(f'TREE {"  " * b.parent.name.count(".") if b.parent else ""}{b.name} parent={b.parent.name if b.parent else "-"} use_deform={b.use_deform} hide={b.hide}')
