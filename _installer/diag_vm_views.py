"""Render the viewmodel from four directions and stitch one 2x2 montage.

The eye-camera preview came back almost empty, so the model's real rest layout has to be read
off orthographic-ish views before ViewModelOffset can be chosen from numbers.
"""

import math
import os

import bpy
from mathutils import Vector

SOURCE = os.path.abspath("game/Assets/Art/ViewModel/CSSArmsArmed.fbx")
OUT = os.path.abspath("_installer/vm_views.png")
TILE = 512
LABELS = ("front", "right", "back", "top")


def to_unity(v):
    return Vector((v.x, v.z, -v.y))


def mesh_points():
    points = []
    for obj in bpy.context.scene.objects:
        if obj.type != 'MESH':
            continue
        world = obj.matrix_world
        stride = max(1, len(obj.data.vertices) // 4096)
        points.extend(world @ v.co for v in list(obj.data.vertices)[::stride])
    return points


def camera_view(name, location, target, lens=50.0):
    data = bpy.data.cameras.new(name)
    data.sensor_fit = 'HORIZONTAL'
    data.sensor_width = 36.0
    data.lens = lens
    data.clip_start = 0.01
    data.clip_end = 100.0
    obj = bpy.data.objects.new(name, data)
    bpy.context.collection.objects.link(obj)
    obj.location = location
    direction = target - location
    obj.rotation_mode = 'QUATERNION'
    obj.rotation_quaternion = direction.to_track_quat('-Z', 'Y')
    return obj


def main():
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(use_global=False)
    bpy.ops.import_scene.fbx(filepath=SOURCE)

    armature = next(o for o in bpy.context.scene.objects if o.type == 'ARMATURE')
    armature.data.pose_position = 'REST'
    bpy.context.view_layer.update()

    points = mesh_points()
    lows = [min(p[i] for p in points) for i in range(3)]
    highs = [max(p[i] for p in points) for i in range(3)]
    center = Vector(((lows[0] + highs[0]) / 2, (lows[1] + highs[1]) / 2, (lows[2] + highs[2]) / 2))
    radius = max(highs[i] - lows[i] for i in range(3)) * 1.6
    print(f"VIEWS blender-space min={tuple(round(v, 3) for v in lows)} max={tuple(round(v, 3) for v in highs)} "
          f"center={tuple(round(v, 3) for v in center)} radius={radius:.3f}")
    print("VIEWS unity-space center=", tuple(round(v, 3) for v in to_unity(center)),
          "min=", tuple(round(v, 3) for v in to_unity(Vector(lows))),
          "max=", tuple(round(v, 3) for v in to_unity(Vector(highs))))

    # Blender axes: +X right, +Y back, +Z up. Unity: +X right, +Y up, +Z forward.
    distance = radius * 1.9
    views = {
        # looking at the model's front (Unity +Z forward == Blender -Y)
        "front": (center + Vector((0, -distance, 0.15)), "from the eye position, gun pointing at camera"),
        "right": (center + Vector((distance, 0, 0.15)), "from the player's right"),
        "back": (center + Vector((0, distance, 0.15)), "behind the shoulders"),
        "top": (center + Vector((0, 0, distance)), "looking down"),
    }

    scene = bpy.context.scene
    scene.render.engine = 'BLENDER_WORKBENCH'
    scene.display.shading.light = 'STUDIO'
    scene.display.shading.color_type = 'SINGLE'
    scene.display.shading.single_color = (0.55, 0.55, 0.55)
    scene.display.shading.show_cavity = True
    scene.render.resolution_x = TILE
    scene.render.resolution_y = TILE
    scene.render.image_settings.file_format = 'PNG'

    paths = []
    for index, name in enumerate(LABELS):
        location, note = views[name]
        camera = camera_view("Cam_" + name, location, center)
        scene.camera = camera
        path = os.path.abspath(f"_installer/vm_tile_{name}.png")
        scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        print(f"VIEWS tile {name} camera={tuple(round(v, 2) for v in location)} {note}")
        paths.append(path)

    tiles = [bpy.data.images.load(p) for p in paths]
    montage = bpy.data.images.new("montage", TILE * 2, TILE * 2, alpha=False)
    width = TILE * 2
    pixels = [0.2] * (width * width * 4)
    for slot, tile in enumerate(tiles):
        origin_x = (slot % 2) * TILE
        origin_y = (1 - slot // 2) * TILE
        source = list(tile.pixels[:])
        for y in range(TILE):
            for x in range(TILE):
                src = (y * TILE + x) * 4
                dst = ((origin_y + y) * width + origin_x + x) * 4
                pixels[dst:dst + 4] = source[src:src + 4]
    montage.pixels = pixels
    montage.filepath_raw = OUT
    montage.file_format = 'PNG'
    montage.save()
    print("VIEWS wrote", OUT)
    for path in paths:
        os.remove(path)


main()
