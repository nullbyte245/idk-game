import json
import sys

import bpy

warrior = sys.argv[sys.argv.index('--warrior') + 1]
glb = sys.argv[sys.argv.index('--glb') + 1]

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=warrior)
bpy.ops.import_scene.gltf(filepath=glb)

report = {'armatures': [], 'meshes': [], 'images': []}
for obj in bpy.data.objects:
    if obj.type == 'ARMATURE':
        anim = obj.animation_data
        report['armatures'].append({
            'name': obj.name,
            'action': anim.action.name if anim and anim.action else None,
            'nla': [s.action.name for t in anim.nla_tracks for s in t.strips if s.action] if anim else [],
            'bones': [b.name for b in obj.data.bones],
        })
    elif obj.type == 'MESH':
        report['meshes'].append({
            'name': obj.name,
            'parent': obj.parent.name if obj.parent else None,
            'materials': [m.name for m in obj.data.materials],
            'verts': len(obj.data.vertices),
            'groups': list(obj.vertex_groups.keys())[:12],
            'groupCount': len(obj.vertex_groups),
            'modifiers': [(m.name, m.type) for m in obj.modifiers],
        })

report['actions'] = [a.name for a in bpy.data.actions]
report['images'] = [i.name for i in bpy.data.images if not i.name.startswith('Render')]
print('DUMP_START')
print(json.dumps(report, indent=1))
print('DUMP_END')
