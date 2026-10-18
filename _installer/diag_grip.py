"""Measure how well the mounted rifle lines up with the CSS hands.

The eye-camera renders cannot tell a grip from a near-miss, so the check is numeric: the distance
from each hand's knuckle bones to the nearest vertex of the gun mesh. A real grip is under 0.02 m;
anything above 0.05 m reads as an open hand holding nothing.
"""

import os

import bpy
from mathutils import Vector

SOURCE = os.path.abspath("game/Assets/Art/ViewModel/CSSArmsArmed.fbx")
GUN = "AK47_ViewModel"

PROBES = (
    "v_weapon.Right_Hand", "v_weapon.Right_Pinky01", "v_weapon.Right_Index01",
    "v_weapon.Right_Thumb01", "v_weapon.Left_Hand", "v_weapon.Left_Pinky01",
    "v_weapon.Left_Index01", "v_weapon.Left_Thumb01",
)


def main():
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(use_global=False)
    bpy.ops.import_scene.fbx(filepath=SOURCE)

    armature = next(o for o in bpy.context.scene.objects if o.type == 'ARMATURE')
    armature.data.pose_position = 'REST'
    bpy.context.view_layer.update()

    gun = bpy.data.objects.get(GUN)
    if gun is None:
        print("GRIP FAIL no", GUN)
        return
    gun_world = gun.matrix_world
    # Sample the gun surface coarsely; the grip and forend are dense enough at this stride.
    surface = [gun_world @ v.co for v in list(gun.data.vertices)[::3]]
    print(f"GRIP gun vertices sampled={len(surface)}")

    for name in PROBES:
        bone = armature.pose.bones.get(name)
        if bone is None:
            print("GRIP missing bone", name)
            continue
        head = armature.matrix_world @ bone.head
        best = min(surface, key=lambda p: (p - head).length_squared)
        delta = head - best
        print(f"GRIP {name} bone={tuple(round(v, 3) for v in head)} nearest={tuple(round(v, 3) for v in best)} "
              f"dist={delta.length:.4f} dir={tuple(round(v, 3) for v in delta.normalized())}")


main()
