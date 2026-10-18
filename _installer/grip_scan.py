"""Finds the AK's grip and forend from the mesh itself so the hands can be posed against real targets.

The rendered previews showed both hands lying flat with fingers extended, hovering over the magazine:
the pistol grip and the handguard were empty. Closing a hand is only correct if it closes around
something with a known centre and radius, so this pass slices the gun along its long axis and reports
what the metal occupies per slice, plus the local axes of every finger bone (which axis curls a
finger is the difference between a wrap and a broken knuckle).

  blender -b --python grip_scan.py -- --arms _grip/CSSArms.fbx --gun _grip/AK47.fbx
"""
import math
import os
import sys

from mathutils import Euler, Matrix, Vector

import bpy

VIEW_POSE = {
    'bone': 'v_weapon',
    'offset': Vector((-0.1196, -0.2137, -0.0185)),
    'pitch': -4.0,
    'roll': 0.0,
    'yaw': 6.0,
    'scale': 1.0,
}
ARMS_EDIT = {'bone': 'v_weapon.Left_Arm', 'offset': Vector((-0.18, -0.24, 0.06))}
FINGERS = ('Index', 'Middle', 'Ring', 'Pinky', 'Thumb')


def arg(name):
    return sys.argv[sys.argv.index(name) + 1]


def build():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=os.path.abspath(arg('--arms')))
    arm = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
    meshes = [o for o in bpy.data.objects if o.type == 'MESH' and o.parent == arm]
    if arm.animation_data:
        arm.animation_data.action = None
        for track in list(arm.animation_data.nla_tracks):
            arm.animation_data.nla_tracks.remove(track)
    arm.data.pose_position = 'POSE'

    bpy.ops.import_scene.fbx(filepath=os.path.abspath(arg('--gun')))
    known = {m.name for m in meshes} | {arm.name}
    gun = next(o for o in bpy.data.objects if o.type == 'MESH' and o.name not in known)

    left = arm.pose.bones.get(ARMS_EDIT['bone'])
    left.matrix = Matrix.Translation(ARMS_EDIT['offset']) @ left.matrix
    bpy.context.view_layer.update()
    bpy.ops.object.select_all(action='DESELECT')
    arm.select_set(True)
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode='POSE')
    bpy.ops.pose.armature_apply(selected=False)
    bpy.ops.object.mode_set(mode='OBJECT')

    gun.parent = arm
    gun.parent_type = 'BONE'
    gun.parent_bone = VIEW_POSE['bone']
    gun.matrix_parent_inverse = Matrix.Identity(4)
    head = arm.matrix_world @ arm.pose.bones[VIEW_POSE['bone']].matrix.translation
    extra = Euler((math.radians(VIEW_POSE['pitch']), 0.0, math.radians(VIEW_POSE['yaw'])), 'XYZ').to_matrix().to_4x4()
    base = gun.matrix_world.to_quaternion().to_matrix().to_4x4()
    scale = gun.matrix_world.to_scale() * VIEW_POSE['scale']
    world = Matrix.Translation(head + VIEW_POSE['offset']) @ extra @ base @ Matrix.Diagonal(scale.to_4d())
    gun.matrix_basis = Matrix.Identity(4)
    bpy.context.view_layer.update()
    gun.matrix_basis = gun.matrix_world.inverted() @ world
    bpy.context.view_layer.update()
    return arm, gun


def main():
    arm, gun = build()
    mw = gun.matrix_world
    pts = [mw @ v.co for v in gun.data.vertices]
    ys = [p.y for p in pts]
    lo, hi = min(ys), max(ys)
    print(f'GUN Y span lo={lo:.3f} hi={hi:.3f} verts={len(pts)}')

    # A slice through an AK tells the story on its own: the receiver is a tall, wide slab, the
    # handguard is a narrow tube near the barrel axis, the pistol grip is a short stub hanging low
    # behind the magazine. Reading the z floor per slice separates the three.
    edge = round(lo / 0.10) * 0.10
    y = hi
    while y > edge - 0.1:
        bucket = [p for p in pts if y - 0.05 < p.y <= y + 0.05]
        if len(bucket) >= 8:
            zs = sorted(p.z for p in bucket)
            n = len(zs)
            print(f'SLICE y=[{y - 0.05:+.2f},{y + 0.05:+.2f}] n={n:5d} '
                  f'x=[{min(p.x for p in bucket):+.3f},{max(p.x for p in bucket):+.3f}] '
                  f'z=[{zs[0]:+.3f},{zs[-1]:+.3f}] p10={zs[n // 10]:+.3f} p50={zs[n // 2]:+.3f}')
        y -= 0.05

    def centre(lo_y, hi_y, lo_z, hi_z, label):
        region = [p for p in pts if lo_y < p.y <= hi_y and lo_z < p.z <= hi_z]
        if not region:
            print(f'TARGET {label}: empty')
            return
        n = len(region)
        mid = lambda axis: sorted(p[axis] for p in region)[n // 2]
        print(f'TARGET {label:<7} n={n:5d} mid=({mid(0):+.3f},{mid(1):+.3f},{mid(2):+.3f}) '
              f'x=[{min(p.x for p in region):+.3f},{max(p.x for p in region):+.3f}] '
              f'z=[{min(p.z for p in region):+.3f},{max(p.z for p in region):+.3f}]')

    centre(-0.30, -0.10, -0.20, -0.010, 'GRIP')
    centre(-0.86, -0.60, +0.02, +0.120, 'FOREND')
    centre(-0.60, -0.30, -0.20, -0.020, 'MAG')

    print('--- BONE AXES (curl axis is the one a knuckle actually bends around) ---')
    for bone in sorted(arm.pose.bones, key=lambda b: b.name):
        if not (any(f in bone.name for f in FINGERS)):
            continue
        m = bone.matrix
        head = arm.matrix_world @ m.translation
        tail = arm.matrix_world @ (m @ Vector((0.0, 0.0, bone.length)))
        print(f'BONE {bone.name:<26} head={tuple(round(v, 4) for v in head)} '
              f'tip={tuple(round(v, 4) for v in tail)} len={bone.length * 100:.2f}cm '
              f'x={tuple(round(v, 2) for v in m.col[0])} y={tuple(round(v, 2) for v in m.col[1])} '
              f'z={tuple(round(v, 2) for v in m.col[2])}')

    for name in ('v_weapon.Right_Hand', 'v_weapon.Left_Hand', 'v_weapon.Right_Arm', 'v_weapon.Left_Arm'):
        bone = arm.pose.bones.get(name)
        if bone:
            m = bone.matrix
            print(f'WRIST {name:<24} head={tuple(round(v, 4) for v in arm.matrix_world @ m.translation)} '
                  f'x={tuple(round(v, 2) for v in m.col[0])} y={tuple(round(v, 2) for v in m.col[1])} '
                  f'z={tuple(round(v, 2) for v in m.col[2])} len={bone.length * 100:.2f}cm')


main()
