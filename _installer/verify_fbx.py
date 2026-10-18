"""Re-imports an exported FBX and prints the world span of every mesh, so a bone-parented prop
that came back at the wrong scale is caught here instead of in a built player.

  blender -b --factory-startup --python verify_fbx.py -- --file <fbx>
"""
import sys

from mathutils import Vector

import bpy

path = sys.argv[sys.argv.index('--file') + 1]
bpy.ops.import_scene.fbx(filepath=path)
for arm in [o for o in bpy.data.objects if o.type == 'ARMATURE']:
    print(f'BONES {arm.name} ' + ', '.join(
        f'{b.name}@{tuple(round(v, 3) for v in arm.matrix_world @ b.matrix_local.translation)}'
        for b in arm.data.bones))
for obj in [o for o in bpy.data.objects if o.type == 'MESH']:
    vertices = obj.data.vertices
    corners = [obj.matrix_world @ vertices[i].co for i in range(0, len(vertices), 64)]
    span = [max(axis) - min(axis) for axis in zip(*corners)]
    parent = obj.parent.name if obj.parent else None
    print(f'VERIFY {obj.name} parent={parent} bone={obj.parent_bone if obj.parent_type == "BONE" else "-"} '
          f'span={tuple(round(v, 3) for v in span)} '
          f'center={tuple(round(v, 3) for v in sum(corners, Vector()) / len(corners))}')
