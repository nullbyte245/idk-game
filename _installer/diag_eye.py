"""Diagnostic: is the eye-camera view empty because of the workbench shading mode or the axes?

Renders four (model offset, camera depth) pairs with flat SINGLE shading, the mode that already
produced a correct four-view montage, so the only variable left is the eye-camera mapping.
"""

import math
import os

import bpy
from mathutils import Matrix, Vector

SOURCE = os.path.abspath("game/Assets/Art/ViewModel/CSSArmsArmed.fbx")
OUT = os.path.abspath("_installer/vm_eye_diag.png")
TILE_W, TILE_H = 512, 288
FOV = 72.0

CASES = [
    (Vector((0.02, -0.16, 0.10)), -1.2),
    (Vector((0.02, -0.16, 0.10)), -0.5),
    (Vector((0.02, -0.16, 0.10)), 0.0),
    (Vector((0.0, 0.0, 0.60)), 0.0),
]


def unity_to_blender(offset):
    return Vector((offset.x, -offset.z, offset.y))


def main():
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(use_global=False)
    bpy.ops.import_scene.fbx(filepath=SOURCE)

    armature = next(o for o in bpy.context.scene.objects if o.type == 'ARMATURE')
    armature.data.pose_position = 'REST'
    bpy.context.view_layer.update()
    base_world = armature.matrix_world.copy()
    print("DIAG armature loc=", tuple(round(v, 3) for v in armature.location),
          "rot=", tuple(round(math.degrees(v), 1) for v in armature.rotation_euler),
          "scale=", tuple(round(v, 3) for v in armature.scale),
          "parent=", armature.parent.name if armature.parent else None)
    for obj in bpy.context.scene.objects:
        print("DIAG obj", obj.name, obj.type, "parent=", obj.parent.name if obj.parent else None,
              "parent_bone=", obj.parent_bone, "hide_render=", obj.hide_render,
              "visible_get=", obj.visible_get())

    data = bpy.data.cameras.new("Eye")
    data.sensor_fit = 'VERTICAL'
    data.sensor_width = 36.0
    data.lens = 18.0 / math.tan(math.radians(FOV * 0.5))
    data.clip_start = 0.01
    data.clip_end = 100.0
    camera = bpy.data.objects.new("Eye", data)
    bpy.context.collection.objects.link(camera)
    bpy.context.scene.camera = camera

    scene = bpy.context.scene
    scene.render.engine = 'BLENDER_WORKBENCH'
    scene.render.resolution_x = TILE_W
    scene.render.resolution_y = TILE_H
    scene.render.image_settings.file_format = 'PNG'
    shading = scene.display.shading
    shading.light = 'STUDIO'
    shading.color_type = 'SINGLE'
    shading.single_color = (0.6, 0.6, 0.6)
    shading.show_cavity = True

    paths = []
    for index, (offset, camera_z) in enumerate(CASES):
        armature.matrix_world = Matrix.Translation(unity_to_blender(offset)) @ base_world
        camera.location = unity_to_blender(Vector((0.0, 0.0, camera_z)))
        bpy.context.view_layer.update()
        path = os.path.abspath(f"_installer/vm_diag_{index}.png")
        scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        print(f"DIAG case {index} offset={tuple(round(v, 2) for v in offset)} cam_z={camera_z} -> {path}")
        paths.append(path)

    tiles = [bpy.data.images.load(p) for p in paths]
    width = TILE_W * 2
    montage = bpy.data.images.new("vm_diag", width, TILE_H * 2, alpha=False)
    pixels = [0.15] * (width * TILE_H * 2 * 4)
    for slot, tile in enumerate(tiles):
        origin_x = (slot % 2) * TILE_W
        origin_y = (1 - slot // 2) * TILE_H
        source = list(tile.pixels[:])
        for y in range(TILE_H):
            row_src = y * TILE_W * 4
            for x in range(TILE_W):
                src = row_src + x * 4
                dst = ((origin_y + y) * width + origin_x + x) * 4
                pixels[dst:dst + 4] = source[src:src + 4]
    montage.pixels = pixels
    montage.filepath_raw = OUT
    montage.file_format = 'PNG'
    montage.save()
    print("DIAG wrote", OUT)
    for path in paths:
        os.remove(path)


main()
