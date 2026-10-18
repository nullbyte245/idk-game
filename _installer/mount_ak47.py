"""Puts the AK-47 into the agent's hands and writes the FBX Unity reads:

  SasAgent + gun -> Assets/Art/Weapons/GunMount.fbx  (the hand pose for the world model)

The first person viewmodel is a separate job: grip_pose.py wraps the CSS hands around the rifle and
exports Assets/Art/ViewModel/CSSArmsArmed.fbx from there, because that pose is solved against the
mounted gun and cannot be authored from the idle rig.

Neither file carries animation. GunMount.fbx exists only so that SceneBuilder can read the gun's
local transform off a bone of the real player rig and re-parent a gun instance with it; the clips
stay in SasAgent.fbx, which is never re-exported here (its take names are longer than Blender's
action-name limit and would come back as "…|Chara", breaking the animator builder's matching).

The pose is authored against the rest pose, which is what a bone-parented child is measured from in
Unity too. Weapon.R is unusable: retarget_sas.py only moves bones present in sas_bonemap.json, so
the Warrior's weapon bone drifts to the origin on the SAS skeleton, while Fist.R sits in the hand.
Pitch is about world X (muzzle up), yaw about world Z (muzzle across the body), roll about world Y.

  blender -b --python mount_ak47.py -- --rig <fbx> --arms <fbx> --gun <fbx>
                                       --out-mount <fbx> --out-view <fbx> --preview <dir>
"""
import math
import os
import sys

from mathutils import Euler, Matrix, Vector

import bpy

BODY_POSE = {
    'bone': 'Fist.R',
    'offset': Vector((0.01, 0.0, -0.03)),
    'pitch': 10.0,
    'roll': 0.0,
    'yaw': 22.0,
    'scale': 1.0,
}


def arg(name):
    return sys.argv[sys.argv.index(name) + 1]


def bone_head(armature, bone_name):
    bone = armature.data.bones.get(bone_name)
    if bone is None:
        raise SystemExit(f'BONE missing {bone_name} on {armature.name} '
                         f'have=[{", ".join(b.name for b in armature.data.bones)[:400]}]')
    pose = armature.pose.bones.get(bone_name)
    return armature.matrix_world @ pose.matrix.translation, bone


def mount(obj, armature, pose):
    head, bone = bone_head(armature, pose['bone'])
    base = obj.matrix_world.to_quaternion().to_matrix().to_4x4()
    # The gun mesh is authored in centimetres and carries a 0.01 object scale, so the mount has
    # to keep that scale: dropping it puts a 90 metre rifle in the hand.
    scale = obj.matrix_world.to_scale() * pose['scale']
    extra = Euler((math.radians(pose['pitch']), math.radians(pose['roll']),
                   math.radians(pose['yaw'])), 'XYZ').to_matrix().to_4x4()
    world = (Matrix.Translation(head + pose['offset']) @ extra @ base
             @ Matrix.Diagonal(scale.to_4d()))

    obj.parent = armature
    obj.parent_type = 'BONE'
    obj.parent_bone = bone.name
    obj.matrix_parent_inverse = Matrix.Identity(4)
    # Writing Object.matrix_world is unreliable across a bone parent: the armature carries a
    # centimetre-to-metre scale, and the setter bakes its inverse into the local scale, so the
    # mesh comes out 100x too big. Read the bone's real world matrix with an identity basis
    # instead and solve for the basis that lands the gun where it belongs.
    obj.matrix_basis = Matrix.Identity(4)
    bpy.context.view_layer.update()
    obj.matrix_basis = obj.matrix_world.inverted() @ world
    bpy.context.view_layer.update()
    print(f'MOUNT {obj.name} bone={bone.name} head={tuple(round(v, 3) for v in head)} '
          f'pos={tuple(round(v, 3) for v in obj.matrix_basis.translation)} '
          f'scale={tuple(round(v, 3) for v in obj.matrix_basis.to_scale())} '
          f'world={tuple(round(v, 3) for v in obj.matrix_world.translation)}')


def render_from(name, location, target, lens, show):
    """Render one preview: only the objects belonging to that shot stay visible, so the floating
    viewmodel hands never turn up in the body frame and vice versa."""
    for obj in bpy.data.objects:
        if obj.type == 'MESH':
            obj.hide_render = obj.name not in show
    cam = bpy.data.cameras.new(name)
    cam.lens = lens
    cam.clip_end = 60.0
    obj = bpy.data.objects.new(name, cam)
    obj.location = location
    obj.rotation_euler = (Vector(target) - Vector(location)).to_track_quat('-Z', 'Y').to_euler()
    bpy.context.scene.collection.objects.link(obj)
    bpy.context.scene.camera = obj
    bpy.context.view_layer.update()
    print(f'CAM {name} loc={tuple(round(v, 3) for v in obj.location)} '
          f'aim={tuple(round(v, 3) for v in target)} '
          f'dist={round((Vector(location) - Vector(target)).length, 3)} show={show}')
    bpy.ops.render.render(write_still=True)
    print(f'RENDER {name} -> {bpy.context.scene.render.filepath}')


def export(path, objects, animate):
    bpy.ops.object.select_all(action='DESELECT')
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.export_scene.fbx(filepath=path, use_selection=True,
                             object_types={'ARMATURE', 'MESH'}, add_leaf_bones=False,
                             bake_anim=animate, bake_anim_use_all_actions=animate,
                             bake_anim_use_nla_strips=False, path_mode='AUTO',
                             embed_textures=False, mesh_smooth_type='FACE',
                             use_mesh_modifiers=False, armature_nodetype='NULL')
    print(f'EXPORTED {path}')


def world_corners(obj, stride=64):
    """Read the bounds off the mesh data: an object bound box already has the parent-inverse
    baked in, so a bone-parented mesh reports a box scaled by the inverse of its armature."""
    vertices = obj.data.vertices
    return [obj.matrix_world @ vertices[i].co for i in range(0, len(vertices), stride)]


def body_extent(meshes, label):
    """The preview camera has to be framed on measured geometry, not on guessed sizes."""
    corners = [c for m in meshes for c in world_corners(m)]
    if not corners:
        raise SystemExit(f'EXTENT no meshes to measure in {[m.name for m in meshes]}')
    center = sum(corners, Vector()) / len(corners)
    span = [max(axis) - min(axis) for axis in zip(*corners)]
    print(f'EXTENT {label} {[m.name for m in meshes]} center={tuple(round(v, 3) for v in center)} '
          f'span={tuple(round(v, 3) for v in span)}')
    return center, max(span)


def main():
    rig_path, arms_path, gun_path = arg('--rig'), arg('--arms'), arg('--gun')
    # Blender resolves a relative render filepath against its own startup directory, not the project.
    preview = os.path.abspath(arg('--preview'))
    out_mount = os.path.abspath(arg('--out-mount'))

    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=rig_path)
    rig_arm = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
    rig_meshes = [o for o in bpy.data.objects if o.type == 'MESH' and o.parent == rig_arm]
    print(f'RIG arm={rig_arm.name} meshes={[m.name for m in rig_meshes]}')
    # The SAS file's take names are mangled by the round trip, so its clips are dropped outright:
    # the mount is authored and exported against the rest pose only. Clearing the action is not
    # enough - the imported NLA strip keeps posing the bones - so the armature is switched to REST.
    if rig_arm.animation_data:
        rig_arm.animation_data.action = None
        for track in list(rig_arm.animation_data.nla_tracks):
            rig_arm.animation_data.nla_tracks.remove(track)
    rig_arm.data.pose_position = 'REST'
    bpy.context.scene.frame_set(1)

    bpy.ops.import_scene.fbx(filepath=arms_path)
    arms_arm = next(o for o in bpy.data.objects if o.type == 'ARMATURE' and o != rig_arm)
    arms_meshes = [o for o in bpy.data.objects if o.type == 'MESH' and o.parent == arms_arm]
    print(f'ARMS arm={arms_arm.name} meshes={[m.name for m in arms_meshes]}')

    bpy.ops.import_scene.fbx(filepath=gun_path)
    known = {m.name for m in rig_meshes + arms_meshes}
    gun = next(o for o in bpy.data.objects if o.type == 'MESH' and o.name not in known)
    print(f'GUN {gun.name} dims={tuple(round(v, 3) for v in gun.dimensions)}')
    body_extent([gun], 'pre-mount')

    # Sketchfab packs leave a degenerate unparented Cube behind; it would ride into Unity.
    for obj in [o for o in bpy.data.objects
                if o.type == 'MESH' and o.parent is None and o != gun]:
        print(f'JUNK removed {obj.name}')
        bpy.data.objects.remove(obj, do_unlink=True)

    left = arms_arm.pose.bones.get('v_weapon.Left_Arm')
    print(f'ARMS {arms_arm.name} left arm head='
          f'{tuple(round(v, 3) for v in arms_arm.matrix_world @ left.matrix.translation)}')

    mount(gun, rig_arm, BODY_POSE)
    bpy.context.view_layer.update()

    os.makedirs(preview, exist_ok=True)
    # Workbench, not EEVEE: a headless Blender has no GPU context and renders EEVEE frames black.
    bpy.context.scene.render.engine = 'BLENDER_WORKBENCH'
    bpy.context.scene.display.shading.light = 'STUDIO'
    bpy.context.scene.display.shading.color_type = 'MATERIAL'
    bpy.context.scene.render.resolution_x = 900
    bpy.context.scene.render.resolution_y = 700

    hand = bone_head(rig_arm, BODY_POSE['bone'])[0]
    body_extent(rig_meshes, 'rig')
    gun_center, gun_span = body_extent([gun], 'gun')
    print(f'BODY hand={tuple(round(v, 3) for v in hand)} center={tuple(round(v, 3) for v in gun_center)} '
          f'span={round(gun_span, 3)}')
    bpy.context.scene.render.filepath = os.path.join(preview, 'ak_body.png')
    render_from('CamBody', gun_center + Vector((0.55, -1.0, 0.35)).normalized() * gun_span * 2.4,
                gun_center, 50,
                [m.name for m in rig_meshes if 'Sword' not in m.name] + [gun.name])

    export(out_mount, [rig_arm, gun], False)


main()
