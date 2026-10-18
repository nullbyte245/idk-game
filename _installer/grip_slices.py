"""Slices the mounted AK along its long axis so the support hand can be put on metal the arm can
actually reach.

The left hand has to sit within 21.8 cm of its shoulder anchor plus the palm, and the handguard sits
farther forward than that, which is what tore the sleeve into a sheet. This prints what the gun
occupies per slice so the target is chosen from geometry instead of from a guess about where a
handguard starts. The gun is mounted with the same transform grip_pose.py uses, so every number here
is in the frame the pose is authored against.

  blender -b --python grip_slices.py -- --arms _grip/CSSArms.fbx --gun _grip/AK47.fbx
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


def arg(name):
    return sys.argv[sys.argv.index(name) + 1]


def bounds(points, axis):
    values = sorted(p[axis] for p in points)
    return values[0], values[len(values) // 2], values[-1]


def main():
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

    left = arm.pose.bones[ARMS_EDIT['bone']]
    left.matrix = Matrix.Translation(ARMS_EDIT['offset']) @ left.matrix
    bpy.ops.object.select_all(action='DESELECT')
    arm.select_set(True)
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode='POSE')
    bpy.ops.pose.armature_apply(selected=False)
    bpy.ops.object.mode_set(mode='OBJECT')

    head = arm.matrix_world @ arm.pose.bones[VIEW_POSE['bone']].matrix.translation
    extra = Euler((math.radians(VIEW_POSE['pitch']), 0.0, math.radians(VIEW_POSE['yaw'])),
                  'XYZ').to_matrix().to_4x4()
    base = gun.matrix_world.to_quaternion().to_matrix().to_4x4()
    world = (Matrix.Translation(head + VIEW_POSE['offset']) @ extra @ base
             @ Matrix.Diagonal(gun.matrix_world.to_scale().to_4d()))
    gun.parent = arm
    gun.parent_type = 'BONE'
    gun.parent_bone = VIEW_POSE['bone']
    gun.matrix_parent_inverse = Matrix.Identity(4)
    gun.matrix_basis = Matrix.Identity(4)
    bpy.context.view_layer.update()
    gun.matrix_basis = gun.matrix_world.inverted() @ world
    bpy.context.view_layer.update()

    pts = [gun.matrix_world @ v.co for v in gun.data.vertices]
    ys = sorted(p.y for p in pts)
    print(f'GUN {gun.name} verts={len(pts)} y={round(ys[0], 3)}..{round(ys[-1], 3)}')
    for i in range(int((ys[-1] - ys[0]) / 0.05) + 1):
        lo = ys[0] + i * 0.05
        region = [p for p in pts if lo <= p.y < lo + 0.05]
        if not region:
            continue
        x0, xm, x1 = bounds(region, 0)
        z0, zm, z1 = bounds(region, 2)
        print(f'SLICE y=[{lo:+.2f},{lo + 0.05:+.2f}) n={len(region):5d} '
              f'x=[{x0:+.3f},{x1:+.3f}] mid={xm:+.3f} w={x1 - x0:.3f} | '
              f'z=[{z0:+.3f},{z1:+.3f}] mid={zm:+.3f} h={z1 - z0:.3f}')

    for name in ('v_weapon.Left_Arm', 'v_weapon.Left_Hand', 'v_weapon.Left_Middle01',
                 'v_weapon.Right_Arm', 'v_weapon.Right_Hand', 'v_weapon.Right_Middle01'):
        pb = arm.pose.bones.get(name)
        if pb is None:
            print(f'BONE missing {name}')
            continue
        h = arm.matrix_world @ pb.matrix.translation
        print(f'BONE {name:<26} head={tuple(round(v, 3) for v in h)}')
    shoulder = arm.matrix_world @ arm.pose.bones['v_weapon.Left_Arm'].matrix.translation
    wrist = arm.matrix_world @ arm.pose.bones['v_weapon.Left_Hand'].matrix.translation
    knuckle = arm.matrix_world @ arm.pose.bones['v_weapon.Left_Middle01'].matrix.translation
    print(f'REACH left shoulder->wrist={round((wrist - shoulder).length * 100, 1)}cm '
          f'wrist->knuckle={round((knuckle - wrist).length * 100, 1)}cm '
          f'shoulder->knuckle={round((knuckle - shoulder).length * 100, 1)}cm')


main()
