"""Render the first-person viewmodel from the eye camera for several ViewModelOffset candidates.

Unity's camera frame (right, up, forward) is left-handed while Blender's (right, up, backward)
is right-handed, so reproducing the Unity eye view needs an explicit aim plus a horizontal flip:
without the flip the render is mirrored and a chosen offset would land on the wrong side.
"""

import math
import os

import bpy
from mathutils import Matrix, Vector

GAMEPLAY_FOV = 72.0
TILE_W, TILE_H = 640, 360
SOURCE = os.path.abspath("game/Assets/Art/ViewModel/CSSArmsArmed.fbx")
OUT = os.path.abspath("_installer/vm_offsets.png")
TEXTURE_DIR = os.path.abspath("game/Assets/Art/Textures")

# Unity-space offsets for the ViewModel root; the first one mirrors SceneBuilder's constant.
CANDIDATES = [
    Vector((0.02, -0.16, 0.10)),
    Vector((0.16, -0.14, 0.14)),
    Vector((0.24, -0.14, 0.16)),
    Vector((0.20, -0.10, 0.20)),
]


def unity_to_blender(offset):
    return Vector((offset.x, -offset.z, offset.y))


def to_unity(v):
    return Vector((v.x, v.z, -v.y))


def bind_textures():
    names = os.listdir(TEXTURE_DIR)
    for mat in bpy.data.materials:
        if not mat.use_nodes:
            continue
        needle = mat.name.lower().replace(" ", "_")
        match = next((n for n in names if os.path.splitext(n)[0].lower() == needle), None)
        if not match:
            continue
        image = bpy.data.images.load(os.path.join(TEXTURE_DIR, match))
        nodes = mat.node_tree.nodes
        target = next((n for n in nodes if n.type == 'TEX_IMAGE'), None)
        if target is None:
            shader = next((n for n in nodes if n.type == 'BSDF_PRINCIPLED'), None)
            if shader is None:
                continue
            target = nodes.new('ShaderNodeTexImage')
            mat.node_tree.links.new(target.outputs['Color'], shader.inputs['Base Color'])
        target.image = image
        print(f"PREVIEW bound {match} -> {mat.name}")


def projected_uv(points, tan_half, aspect):
    """Screen UV of Unity-space points seen by the eye camera at the origin looking along +Z."""
    us, vs = [], []
    for p in points:
        if p.z < 0.05:
            continue
        us.append(0.5 + (p.x / p.z) / (2 * tan_half * aspect))
        vs.append(0.5 + (p.y / p.z) / (2 * tan_half))
    if not us:
        return None
    return (round(min(us), 2), round(min(vs), 2), round(max(us), 2), round(max(vs), 2))


def main():
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(use_global=False)
    for block in (bpy.data.meshes, bpy.data.armatures, bpy.data.materials):
        for orphan in list(block):
            if orphan.users == 0:
                block.remove(orphan)
    bpy.ops.import_scene.fbx(filepath=SOURCE)

    armature = next(o for o in bpy.context.scene.objects if o.type == 'ARMATURE')
    armature.data.pose_position = 'REST'
    bpy.context.view_layer.update()
    base_world = armature.matrix_world.copy()
    local_points = {o.name: [base_world.inverted() @ o.matrix_world @ v.co
                             for v in list(o.data.vertices)[::max(1, len(o.data.vertices) // 2048)]]
                    for o in bpy.context.scene.objects if o.type == 'MESH'}
    bind_textures()

    camera_data = bpy.data.cameras.new("Eye")
    camera_data.sensor_fit = 'VERTICAL'
    camera_data.sensor_width = 36.0
    camera_data.lens = 18.0 / math.tan(math.radians(GAMEPLAY_FOV * 0.5))
    camera_data.clip_start = 0.01
    camera_data.clip_end = 50.0
    camera = bpy.data.objects.new("Eye", camera_data)
    bpy.context.collection.objects.link(camera)
    # Blender cameras look down their local -Z; aim that at the model's forward direction.
    camera.rotation_mode = 'QUATERNION'
    camera.rotation_quaternion = Vector((0.0, -1.0, 0.0)).to_track_quat('-Z', 'Y')
    bpy.context.scene.camera = camera

    render = bpy.context.scene.render
    render.engine = 'BLENDER_WORKBENCH'
    render.resolution_x = TILE_W
    render.resolution_y = TILE_H
    render.image_settings.file_format = 'PNG'
    shading = bpy.context.scene.display.shading
    shading.light = 'STUDIO'
    shading.color_type = 'TEXTURE'
    shading.show_cavity = True

    tan_half = math.tan(math.radians(GAMEPLAY_FOV * 0.5))
    paths = []
    for index, offset in enumerate(CANDIDATES):
        armature.matrix_world = Matrix.Translation(unity_to_blender(offset)) @ base_world
        bpy.context.view_layer.update()
        for name, pts in local_points.items():
            world = [to_unity(armature.matrix_world @ p) for p in pts]
            print(f"PREVIEW offset={tuple(round(v, 2) for v in offset)} mesh={name} "
                  f"uv_bbox=(min_u,min_v,max_u,max_v)={projected_uv(world, tan_half, TILE_W / TILE_H)}")
        path = os.path.abspath(f"_installer/vm_off_{index}.png")
        render.filepath = path
        bpy.ops.render.render(write_still=True)
        paths.append(path)

    tiles = [bpy.data.images.load(p) for p in paths]
    width = TILE_W * 2
    montage = bpy.data.images.new("vm_offsets", width, TILE_H * 2, alpha=False)
    pixels = [0.15] * (width * TILE_H * 2 * 4)
    for slot, tile in enumerate(tiles):
        origin_x = (slot % 2) * TILE_W
        origin_y = (1 - slot // 2) * TILE_H
        source = list(tile.pixels[:])
        for y in range(TILE_H):
            for x in range(TILE_W):
                src = (y * TILE_W + (TILE_W - 1 - x)) * 4
                dst = ((origin_y + y) * width + origin_x + x) * 4
                pixels[dst:dst + 4] = source[src:src + 4]
    montage.pixels = pixels
    montage.filepath_raw = OUT
    montage.file_format = 'PNG'
    montage.save()
    print("PREVIEW wrote", OUT)
    for path in paths:
        os.remove(path)


main()
