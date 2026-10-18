"""Report the viewmodel's bounds in Unity coordinates.

Unity's FBX import applies the Blender->Unity axis swap (x, y, z)_blender -> (x, z, -y)_unity,
so converting the Blender world-space vertices gives exactly what the eye camera sees when the
ViewModel root sits at the origin.
"""

import os
import sys

import bpy
from mathutils import Vector

SOURCE = os.path.abspath("game/Assets/Art/ViewModel/CSSArmsArmed.fbx")


def to_unity(v):
    return Vector((v.x, v.z, -v.y))


def corners(obj):
    mesh = obj.data
    world = obj.matrix_world
    stride = max(1, len(mesh.vertices) // 4096)
    return [to_unity(world @ v.co) for v in list(mesh.vertices)[::stride]]


def merge(points):
    lows = [min(p[i] for p in points) for i in range(3)]
    highs = [max(p[i] for p in points) for i in range(3)]
    return lows, highs


def report(label, points):
    lows, highs = merge(points)
    size = [round(highs[i] - lows[i], 3) for i in range(3)]
    center = [round((highs[i] + lows[i]) / 2, 3) for i in range(3)]
    print(f"VM {label} min={[round(v, 3) for v in lows]} max={[round(v, 3) for v in highs]} "
          f"size={size} center={center}")


def main():
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(use_global=False)
    bpy.ops.import_scene.fbx(filepath=SOURCE)

    armature = next((o for o in bpy.context.scene.objects if o.type == 'ARMATURE'), None)
    if armature is None:
        print("VM FAIL no armature")
        return
    armature.data.pose_position = 'REST'
    bpy.context.view_layer.update()

    all_points = []
    for obj in bpy.context.scene.objects:
        if obj.type != 'MESH':
            continue
        points = corners(obj)
        all_points.extend(points)
        report(obj.name, points)
    if all_points:
        report("ALL", all_points)

    for name in ("v_weapon.Left_Hand", "v_weapon.Right_Hand", "v_weapon", "v_weapon.Left_Arm",
                 "v_weapon.Right_Arm", "v_weapon.Spine", "v_weapon.Head"):
        bone = armature.pose.bones.get(name)
        if bone is None:
            continue
        head = to_unity(armature.matrix_world @ bone.head)
        tail = to_unity(armature.matrix_world @ bone.tail)
        print(f"VM BONE {name} head={tuple(round(v, 3) for v in head)} tail={tuple(round(v, 3) for v in tail)}")

    for bone in armature.pose.bones:
        print("VM ALLBONES", bone.name, tuple(round(v, 3) for v in to_unity(armature.matrix_world @ bone.head)))


main()
