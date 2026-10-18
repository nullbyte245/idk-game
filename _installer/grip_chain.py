"""Prints the arms rig's bone chain so the wrap stops guessing which bone bends an elbow.

  blender -b --python grip_chain.py -- --arms _grip/CSSArms.fbx
"""
import os
import sys

from mathutils import Vector

import bpy


def arg(name):
    return sys.argv[sys.argv.index(name) + 1]


def main():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=os.path.abspath(arg('--arms')))
    arm = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
    if arm.animation_data:
        arm.animation_data.action = None
        for track in list(arm.animation_data.nla_tracks):
            arm.animation_data.nla_tracks.remove(track)
    arm.data.pose_position = 'POSE'
    bpy.context.view_layer.update()

    print(f'ARM {arm.name} bones={len(arm.data.bones)}')
    for bone in arm.data.bones:
        if 'Arm' in bone.name or 'Hand' in bone.name or 'Shoulder' in bone.name \
                or 'Root3' in bone.name or 'Parent' in bone.name or bone.name == 'v_weapon':
            head = arm.matrix_world @ bone.head_local
            tail = arm.matrix_world @ bone.tail_local
            print(f'  {bone.name:<28} parent={str(bone.parent.name if bone.parent else "-"):<26} '
                  f'children={[c.name for c in bone.children]} '
                  f'len={round((tail - head).length * 100, 1)}cm '
                  f'head={tuple(round(v, 3) for v in head)}')

    mesh = next(o for o in bpy.data.objects if o.type == 'MESH' and o.parent == arm)
    print(f'MESH {mesh.name} verts={len(mesh.data.vertices)} groups={len(mesh.vertex_groups)}')
    for group in mesh.vertex_groups:
        weights = [v.weight for e in mesh.data.vertices for v in e.groups if v.group == group.index]
        if not weights:
            continue
        pts = [mesh.matrix_world @ mesh.data.vertices[e.vertex_index].co
               for e in mesh.data.vertices for g in e.groups
               if g.group == group.index and g.weight > 0.5]
        if not pts:
            continue
        mid = sum(pts, Vector()) / len(pts)
        print(f'  {group.name:<28} n={len(pts):5d} max={max(weights):.2f} '
              f'mid={tuple(round(v, 3) for v in mid)}')


main()
