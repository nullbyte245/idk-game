"""Renders the exported FBX back inside Blender so a broken rig can be attributed to the
conversion instead of guessed at, and prints the armature hierarchy that drives it."""
import json
import math
import sys

from mathutils import Vector

import bpy

fbx = sys.argv[sys.argv.index('--fbx') + 1]
out = sys.argv[sys.argv.index('--out') + 1]
frame = int(sys.argv[sys.argv.index('--frame') + 1]) if '--frame' in sys.argv else 1

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=fbx)

arm = [o for o in bpy.data.objects if o.type == 'ARMATURE'][0]
meshes = [o for o in bpy.data.objects if o.type == 'MESH']
print('BONES ' + json.dumps([
    {'n': b.name, 'p': b.parent.name if b.parent else None,
     'head': [round(c, 3) for c in b.head_local],
     'len': round(b.length, 3)} for b in arm.data.bones]))
print('MESHES ' + json.dumps([{'n': m.name, 'parent': m.parent.name if m.parent else None,
                               'groups': len(m.vertex_groups),
                               'dims': [round(c, 2) for c in m.dimensions]} for m in meshes]))
print('ACTIONS ' + json.dumps([a.name for a in bpy.data.actions]))

if frame > 1 and bpy.data.actions:
    action = next((a for a in bpy.data.actions if 'Idle' in a.name), bpy.data.actions[0])
    arm.animation_data_create()
    arm.animation_data.action = action
    scene = bpy.context.scene
    scene.frame_start = action.frame_range[0]
    scene.frame_end = action.frame_range[1]
    scene.frame_set(int((scene.frame_start + scene.frame_end) * 0.4))

# Frame the camera on the world-space bounds: the mesh data and its object transform can be
# in different unit scales, so local dimensions are useless for framing.
corners = [m.matrix_world @ Vector(corner) for m in meshes for corner in m.bound_box]
if not corners:
    raise SystemExit('no mesh to render')
center = sum(corners, Vector()) / len(corners)
extent = max(max(axis) - min(axis) for axis in zip(*corners))
radius = max(extent, 0.1)

cam_data = bpy.data.cameras.new('Cam')
cam_data.clip_end = radius * 50
cam = bpy.data.objects.new('Cam', cam_data)
bpy.context.collection.objects.link(cam)
direction = Vector((0.35, -1.0, 0.18)).normalized()
cam.location = center + direction * radius * 2.2
cam.rotation_euler = (-direction).to_track_quat('-Z', 'Y').to_euler()
bpy.context.scene.camera = cam

world = bpy.data.worlds.new('Flat')
world.use_nodes = True
world.node_tree.nodes['Background'].inputs[0].default_value = (0.35, 0.36, 0.4, 1)
bpy.context.scene.world = world
print(f'CAM center={[round(c, 3) for c in center]} extent={round(extent, 3)}')

scene = bpy.context.scene
scene.render.engine = 'BLENDER_WORKBENCH'
scene.display.shading.light = 'STUDIO'
scene.display.shading.color_type = 'MATERIAL'
scene.render.resolution_x = 700
scene.render.resolution_y = 900
scene.render.filepath = out
bpy.ops.render.render(write_still=True)
print('RENDERED ' + out)
