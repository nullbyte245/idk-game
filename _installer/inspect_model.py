"""Prints what a model file actually contains, so an import plan can be made from data
rather than from the file name."""
import json
import sys

import bpy

path = sys.argv[sys.argv.index('--file') + 1]

bpy.ops.wm.read_factory_settings(use_empty=True)
if path.lower().endswith(('.glb', '.gltf')):
    bpy.ops.import_scene.gltf(filepath=path)
else:
    bpy.ops.import_scene.fbx(filepath=path)

report = {'objects': [], 'actions': [a.name for a in bpy.data.actions]}
for obj in bpy.data.objects:
    if obj.type == 'ARMATURE':
        report['objects'].append({'kind': 'armature', 'name': obj.name,
                                  'scale': [round(s, 4) for s in obj.scale],
                                  'bones': [b.name for b in obj.data.bones]})
    elif obj.type == 'MESH':
        report['objects'].append({'kind': 'mesh', 'name': obj.name,
                                  'parent': obj.parent.name if obj.parent else None,
                                  'verts': len(obj.data.vertices),
                                  'materials': [m.name for m in obj.data.materials if m],
                                  'groups': len(obj.vertex_groups),
                                  'dims': [round(c, 3) for c in obj.dimensions]})
print('INSPECT_START')
print(json.dumps(report, indent=1))
print('INSPECT_END')
