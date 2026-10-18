"""Compares one bone's rest frame across two armatures, which decides whether a bone-parented prop
can be authored on the donor rig and transplanted onto the retargeted one.

  blender -b --factory-startup --python compare_bone.py -- --a <fbx> --b <fbx> --bone Weapon.R
"""
import math
import sys

from mathutils import Matrix, Vector

import bpy

a_path, b_path = sys.argv[sys.argv.index('--a') + 1], sys.argv[sys.argv.index('--b') + 1]
wanted = sys.argv[sys.argv.index('--bone') + 1]


def rest(path):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=path)
    arm = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
    bone = arm.data.bones.get(wanted)
    if bone is None:
        return f'{path} ARM={arm.name} MISSING {wanted} have=' + ', '.join(
            b.name for b in arm.data.bones if 'Arm' in b.name or 'Fist' in b.name or 'Weapon' in b.name)
    matrix = arm.matrix_world @ bone.matrix_local
    axis = matrix.to_3x3()
    meshes = [o for o in bpy.data.objects if o.type == 'MESH']
    corners = [o.matrix_world @ Vector(c) for o in meshes for c in o.bound_box]
    span = [round(max(ax) - min(ax), 3) for ax in zip(*corners)] if corners else []
    return (f'{path} ARM={arm.name} objScale={tuple(round(s, 4) for s in arm.scale)}\n'
            f'  {wanted} head={tuple(round(v, 4) for v in matrix.translation)} '
            f'len={round(bone.length, 4)} '
            f'x={tuple(round(v, 3) for v in axis.col[0])} '
            f'y={tuple(round(v, 3) for v in axis.col[1])} '
            f'z={tuple(round(v, 3) for v in axis.col[2])} meshSpan={span}')


first = rest(a_path)
second = rest(b_path)
print('A ' + first)
print('B ' + second)
