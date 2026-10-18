import bpy
from mathutils import Vector
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=__import__('sys').argv[sys_index := __import__('sys').argv.index('--glb') + 1])
arm = [o for o in bpy.data.objects if o.type == 'ARMATURE'][0]
for m in [o for o in bpy.data.objects if o.type == 'MESH']:
    corners = [m.matrix_world @ Vector(c) for c in m.bound_box]
    ext = [round(max(a) - min(a), 3) for a in zip(*corners)]
    print(f'MESH {m.name} worldExtent={ext} scale={tuple(round(s,4) for s in m.scale)} parent={m.parent.name if m.parent else None}')
print(f'ARM {arm.name} scale={tuple(round(s,4) for s in arm.scale)} loc={tuple(round(c,3) for c in arm.location)}')
h = (arm.matrix_world @ arm.data.bones['head_0'].matrix_local).translation
p = (arm.matrix_world @ arm.data.bones['pelvis'].matrix_local).translation
print(f'SKELETON hipsToHead={(h - p).length:.3f} headWorld={tuple(round(c,3) for c in h)}')
