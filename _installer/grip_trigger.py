"""Locates the AK's trigger and trigger guard in the mounted frame so the index finger can be aimed
at real metal instead of at a guess beside the grip.

The right hand already wraps the pistol grip, but every finger including the index is curled into the
same handful, which is what the in-game viewmodel looks wrong. Straightening the index only puts it
on the trigger if the trigger's position is known, so this prints the geometry under the receiver in
a fine grid: the guard is a thin loop, the blade is a small dense cluster inside it.

  blender -b --python grip_trigger.py -- --arms _grip/CSSArms.fbx --gun _grip/AK47.fbx
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


def arg(name):
    return sys.argv[sys.argv.index(name) + 1]


def main():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=os.path.abspath(arg('--arms')))
    arm = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
    meshes = {o.name for o in bpy.data.objects if o.type == 'MESH'}
    if arm.animation_data:
        arm.animation_data.action = None
        for track in list(arm.animation_data.nla_tracks):
            arm.animation_data.nla_tracks.remove(track)
    arm.data.pose_position = 'POSE'

    bpy.ops.import_scene.fbx(filepath=os.path.abspath(arg('--gun')))
    gun = next(o for o in bpy.data.objects if o.type == 'MESH' and o.name not in meshes)

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
    print(f'GUN {gun.name} verts={len(pts)}')

    # Under the receiver, ahead of the grip: the guard loop and the blade inside it.
    box = [p for p in pts if -0.40 <= p.y <= -0.18 and -0.09 <= p.z <= 0.05 and -0.16 <= p.x <= -0.06]
    print(f'BOX n={len(box)}')
    step = 0.02
    for row in range(int((0.05 - -0.09) / step)):
        z1 = -0.09 + (row + 1) * step
        line = []
        for col in range(int((-0.06 - -0.16) / step)):
            x0 = -0.16 + col * step
            n = len([p for p in box if x0 <= p.x < x0 + step and z1 - step <= p.z < z1])
            line.append(f'{n:4d}')
        print(f'  z={z1:+.3f} ' + ' '.join(line))
    print('        x=' + ' '.join(f'{-0.16 + c * step:+.3f}' for c in range(int(0.10 / step))))

    # Slices along the gun tell where the guard front, blade and grip back edge fall in Y.
    for i in range(int(0.22 / 0.01)):
        y0 = -0.40 + i * 0.01
        region = [p for p in pts if y0 <= p.y < y0 + 0.01 and p.z < 0.05 and p.z > -0.09]
        if not region:
            continue
        x = sorted(p.x for p in region)
        z = sorted(p.z for p in region)
        print(f'YSLICE y=[{y0:+.3f},{y0 + 0.01:+.3f}) n={len(region):4d} '
              f'x=[{x[0]:+.3f},{x[-1]:+.3f}] z=[{z[0]:+.3f},{z[-1]:+.3f}]')


main()
