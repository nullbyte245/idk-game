"""Measures the current CSS-hands / AK-47 viewmodel grip so the finger pose is authored against
numbers rather than guesses.

Rebuilds the same scene mount_ak47.py exports (arms rig + left arm slid onto the forend + the gun
bone-parented to v_weapon with the authored VIEW_POSE), then prints for every finger bone: head,
tail, and the closest gun vertex to the tip. A fingertip that should be wrapping the pistol grip has
to come within a centimetre of it; anything further is the finger floating in front of or behind the
metal, which is exactly what reads as "he is holding nothing".

  blender -b --python grip_probe.py -- --arms _grip/CSSArms.fbx --gun _grip/AK47.fbx
                                        --preview _grip/preview
"""
import math
import os
import sys

from mathutils import Euler, Matrix, Vector

import bpy

# Kept identical to mount_ak47.py on purpose: this probe has to measure the grip the player sees,
# not a scene that drifted away from what Unity imported.
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


def build():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=os.path.abspath(arg('--arms')))
    arm = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
    meshes = [o for o in bpy.data.objects if o.type == 'MESH' and o.parent == arm]
    print(f'ARMS arm={arm.name} meshes={[m.name for m in meshes]}')
    if arm.animation_data:
        arm.animation_data.action = None
        for track in list(arm.animation_data.nla_tracks):
            arm.animation_data.nla_tracks.remove(track)
    arm.data.pose_position = 'POSE'

    bpy.ops.import_scene.fbx(filepath=os.path.abspath(arg('--gun')))
    known = {m.name for m in meshes} | {arm.name}
    gun = next(o for o in bpy.data.objects if o.type == 'MESH' and o.name not in known)
    print(f'GUN {gun.name} dims={tuple(round(v, 3) for v in gun.dimensions)}')

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
    extra = Euler((math.radians(VIEW_POSE['pitch']), 0.0, math.radians(VIEW_POSE['yaw'])),
                  'XYZ').to_matrix().to_4x4()
    base = gun.matrix_world.to_quaternion().to_matrix().to_4x4()
    scale = gun.matrix_world.to_scale() * VIEW_POSE['scale']
    world = Matrix.Translation(head + VIEW_POSE['offset']) @ extra @ base @ Matrix.Diagonal(scale.to_4d())
    gun.matrix_basis = Matrix.Identity(4)
    bpy.context.view_layer.update()
    gun.matrix_basis = gun.matrix_world.inverted() @ world
    bpy.context.view_layer.update()
    print(f'MOUNTED gun world={tuple(round(v, 3) for v in gun.matrix_world.translation)}')
    return arm, meshes, gun


def gun_surface(gun, point, limit=4096):
    vertices = gun.data.vertices
    step = max(1, len(vertices) // limit)
    best = None
    for i in range(0, len(vertices), step):
        world = gun.matrix_world @ vertices[i].co
        d = (world - point).length
        if best is None or d < best[0]:
            best = (d, world, i)
    return best


FINGERS = ('Index', 'Middle', 'Ring', 'Pinky', 'Thumb')


def is_finger(name):
    return any(part in name for part in FINGERS) and 'Arm' not in name and 'Hand' not in name


def sampled(points, count):
    step = max(1, len(points) // count)
    return [points[i] for i in range(0, len(points), step)]


def main():
    arm, meshes, gun = build()
    bpy.context.view_layer.update()

    print('BONES ' + ' | '.join(b.name for b in arm.data.bones))

    for bone in sorted(arm.pose.bones, key=lambda b: b.name):
        if not is_finger(bone.name):
            continue
        head = arm.matrix_world @ bone.matrix.translation
        tail = arm.matrix_world @ (bone.matrix @ Vector((0.0, 0.0, bone.length)))
        tip = tail
        d, spot, index = gun_surface(gun, tip)
        print(f'FINGER {bone.name:<28} head={tuple(round(v, 4) for v in head)} '
              f'tip={tuple(round(v, 4) for v in tip)} '
              f'gun_dist={d * 100:.1f}cm at={tuple(round(v, 4) for v in spot)} v={index}')

    corners = [gun.matrix_world @ v.co for v in sampled(list(gun.data.vertices), 800)]
    low = Vector((min(c.x for c in corners), min(c.y for c in corners), min(c.z for c in corners)))
    high = Vector((max(c.x for c in corners), max(c.y for c in corners), max(c.z for c in corners)))
    print(f'GUN BOX low={tuple(round(v, 4) for v in low)} high={tuple(round(v, 4) for v in high)}')
    for bone in ('v_weapon.Right_Hand', 'v_weapon.Left_Hand', 'v_weapon'):
        pose = arm.pose.bones.get(bone)
        if pose:
            print(f'ANCHOR {bone} = {tuple(round(v, 4) for v in arm.matrix_world @ pose.matrix.translation)}')

    preview = os.path.abspath(arg('--preview'))
    os.makedirs(preview, exist_ok=True)
    bpy.context.scene.render.engine = 'BLENDER_WORKBENCH'
    bpy.context.scene.display.shading.light = 'STUDIO'
    bpy.context.scene.display.shading.color_type = 'MATERIAL'
    bpy.context.scene.render.resolution_x = 900
    bpy.context.scene.render.resolution_y = 700

    center = (low + high) / 2
    span = (high - low).length
    shots = {
        'grip_right': (center + Vector((0.55, -0.35, 0.15)).normalized() * span * 1.1, center),
        'grip_left': (center + Vector((-0.55, -0.35, 0.15)).normalized() * span * 1.1, center),
        'grip_eye': (Vector((0.0, -0.55, 0.12)) + center * Vector((0.4, 0.4, 0.4)), center),
        'grip_top': (center + Vector((0.0, 0.0, 1.0)) * span * 1.2, center),
    }
    for name, (location, target) in shots.items():
        cam = bpy.data.cameras.new(name)
        cam.lens = 50
        cam.clip_end = 60.0
        obj = bpy.data.objects.new(name, cam)
        obj.location = location
        obj.rotation_euler = (Vector(target) - Vector(location)).to_track_quat('-Z', 'Y').to_euler()
        bpy.context.scene.collection.objects.link(obj)
        bpy.context.scene.camera = obj
        bpy.context.view_layer.update()
        bpy.context.scene.render.filepath = os.path.join(preview, name + '.png')
        bpy.ops.render.render(write_still=True)
        print(f'RENDER {name} loc={tuple(round(v, 3) for v in location)} -> {bpy.context.scene.render.filepath}')


main()
