"""Re-skin the Sketchfab SAS glb onto the CC0 Warrior armature so the existing Unity
Generic clips (Idle/Walk/Run/Roll/Death) drive it without a Humanoid avatar.

  blender -b --python retarget_sas.py -- --warrior <fbx> --glb <glb> --map <json> --out <fbx>

A Generic retarget only looks right when three things line up, so all three are rebuilt
from the SAS skeleton: bone positions, bone roll (local axes) and the skin weights.
Because the rest pose becomes the SAS rest pose, frame 0 of every clip deforms the mesh
exactly as the model was authored, and the clips animate it from there.
"""
import json
import math
import os
import sys

from mathutils import Vector

import bpy

# glTF material names are Source-internal; Unity binds textures by material name, so both
# sides of the pair get short, stable names.
MATERIAL_NAMES = {
    'ctm_sas_body': 'SAS_Body',
    'ctm_sas_bodylegs': 'SAS_Legs',
    'ctm_sas_head_gasmask': 'SAS_Head',
    'ctm_sas_lenses': 'SAS_Lenses',
    'glove_hardknuckle_black': 'SAS_Gloves',
}


def arg(name):
    return sys.argv[sys.argv.index(name) + 1]


def rest_matrix_in(armature, source, bone):
    """A source bone's rest matrix expressed in the target armature's local space."""
    return armature.matrix_world.inverted() @ source.matrix_world @ bone.matrix_local


def unify_units(warrior, sas, meshes):
    """Put both rigs in the same object space with unit scale before anything is transferred.

    The glb arrives as a centimetre mesh under a 0.0254-scaled armature. A skinned FBX whose
    mesh and armature disagree on relative scale comes back from export blown up, so the
    Warrior object is moved onto the SAS one and both scales are baked into the data.
    """
    warrior.matrix_basis = sas.matrix_basis.copy()
    bpy.context.view_layer.update()

    bpy.ops.object.select_all(action='DESELECT')
    for obj in [warrior, sas] + meshes:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = sas
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    bpy.ops.object.select_all(action='DESELECT')
    print(f'UNITS warriorScale={tuple(round(s, 4) for s in warrior.scale)} '
          f'meshScale={tuple(round(s, 4) for s in meshes[0].scale)}')


def rebuild_rest(warrior, sas, mapping):
    """Move and roll the Warrior bones onto the SAS joints they are taking over.

    Head and tail go through the same mapping so a bone length can never be read in one
    unit system and written in another.
    """
    bpy.context.view_layer.objects.active = warrior
    warrior.select_set(True)
    bpy.ops.object.mode_set(mode='EDIT')
    bones = warrior.data.edit_bones

    placed = 0
    for sas_name, warrior_name in mapping.items():
        source = sas.data.bones.get(sas_name)
        target = bones.get(warrior_name)
        if source is None or target is None:
            print(f'MISS {sas_name} -> {warrior_name}')
            continue

        matrix = rest_matrix_in(warrior, sas, source)
        axis_y = (matrix.to_3x3() @ Vector((0, 1, 0))).normalized()
        target.head = matrix.translation
        target.tail = matrix @ Vector((0, source.length, 0))
        target.use_connect = False

        current = target.matrix
        wanted = (matrix.to_3x3() @ Vector((1, 0, 0))).normalized()
        wanted = (wanted - wanted.dot(axis_y) * axis_y).normalized()
        target.roll = target.roll + math.atan2(wanted.dot(current.col[2].normalized()),
                                               wanted.dot(current.col[0].normalized()))
        placed += 1

    bpy.ops.object.mode_set(mode='OBJECT')
    return placed


def resolve(name, mapping, bones):
    """Mapped bone wins; otherwise the nearest mapped ancestor keeps the weight."""
    current = name
    while current:
        if current in mapping:
            return mapping[current]
        bone = bones.get(current)
        current = bone.parent.name if bone and bone.parent else None
    return None


def vertex_weights(mesh):
    """group name -> [(vertex index, weight)], read once because removing a group renumbers the rest."""
    name_of = {g.index: g.name for g in mesh.vertex_groups}
    weights = {name: [] for name in name_of.values()}
    for vertex in mesh.data.vertices:
        for deform in vertex.groups:
            name = name_of.get(deform.group)
            if name:
                weights[name].append((vertex.index, deform.weight))
    return weights


def reskin(warrior, sas, meshes, mapping):
    moved, merged, dropped = 0, 0, []
    for mesh in meshes:
        weights = vertex_weights(mesh)
        for name in list(weights):
            target = resolve(name, mapping, sas.data.bones)
            group = mesh.vertex_groups.get(name)
            if group is None:
                continue
            if target is None:
                dropped.append(name)
                mesh.vertex_groups.remove(group)
                continue

            if target == name:
                continue
            existing = mesh.vertex_groups.get(target)
            if existing is None:
                group.name = target
                moved += 1
                continue
            # Two SAS joints fold onto one Warrior bone (twist + limb): sum their weights.
            # glTF quantises weights, so batching the vertices by value keeps this to a few calls.
            buckets = {}
            for index, weight in weights[name]:
                buckets.setdefault(round(weight, 5), []).append(index)
            for weight, indices in buckets.items():
                existing.add(indices, weight, 'ADD')
            mesh.vertex_groups.remove(group)
            merged += 1

        for modifier in mesh.modifiers:
            if modifier.type == 'ARMATURE':
                modifier.object = warrior
        mesh.parent = warrior
        mesh.matrix_parent_inverse = warrior.matrix_world.inverted()
    return moved, merged, dropped


def rename_materials(meshes):
    for mesh in meshes:
        for slot, material in enumerate(mesh.data.materials):
            if material is None:
                continue
            clean = MATERIAL_NAMES.get(material.name.split('.')[0])
            if clean is None:
                continue
            material.name = clean
            if mesh.material_slots[slot].name not in ('', clean):
                mesh.material_slots[slot].name = clean


def save_color_textures(meshes, folder):
    """Unity's scene builder binds Assets/Art/Textures/<materialName>.png, so the colour maps
    are written out under exactly those names."""
    saved = []
    for mesh in meshes:
        for material in mesh.data.materials:
            if material is None or material.name not in MATERIAL_NAMES.values():
                continue
            if material.node_tree is None:
                continue
            for node in material.node_tree.nodes:
                if node.type != 'TEX_IMAGE' or node.image is None:
                    continue
                name = node.image.name.lower()
                if 'normal' in name or 'orm' in name or 'ao' in name:
                    continue
                node.image.filepath_raw = os.path.join(folder, material.name + '.png')
                node.image.file_format = 'PNG'
                node.image.save()
                saved.append(material.name)
    return saved


def drop_foreign_actions(keep_prefix):
    """The glb ships 394 eye/jiggle takes; only the Warrior's clips may reach the FBX."""
    removed = 0
    for action in list(bpy.data.actions):
        if action.name.startswith(keep_prefix):
            continue
        bpy.data.actions.remove(action)
        removed += 1
    return removed


def main():
    warrior_path, glb_path, out_path = arg('--warrior'), arg('--glb'), arg('--out')
    mapping = json.load(open(arg('--map'), encoding='utf-8'))

    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=warrior_path)
    bpy.ops.import_scene.gltf(filepath=glb_path)

    arms = [o for o in bpy.data.objects if o.type == 'ARMATURE']
    sas = [a for a in arms if 'pelvis' in a.data.bones][0]
    warrior = [a for a in arms if a is not sas][0]
    sas_meshes = [o for o in bpy.data.objects if o.type == 'MESH' and o.parent == sas]
    print(f'ARMATURES warrior={warrior.name} sas={sas.name} sasMeshes={[m.name for m in sas_meshes]}')

    # The Warrior rig drives its limbs with IK targets that Unity cannot import; dropping the
    # constraints leaves the FK curves the FBX was authored with, which is what the clips need.
    for bone in warrior.pose.bones:
        for constraint in list(bone.constraints):
            bone.constraints.remove(constraint)

    unify_units(warrior, sas, sas_meshes)
    placed = rebuild_rest(warrior, sas, mapping)
    moved, merged, dropped = reskin(warrior, sas, sas_meshes, mapping)
    rename_materials(sas_meshes)
    print(f'REST placed={placed}/{len(mapping)} groupsMoved={moved} merged={merged} dropped={dropped}')

    bpy.data.objects.remove(sas, do_unlink=True)
    purged = drop_foreign_actions(warrior.name)
    print(f'PURGED actions={purged} left={[a.name for a in bpy.data.actions]}')

    for obj in bpy.data.objects:
        obj.select_set(obj is warrior or obj in sas_meshes)
    bpy.context.view_layer.objects.active = warrior

    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    saved = save_color_textures(sas_meshes, arg('--textures'))
    print(f'TEXTURES saved={saved}')

    bpy.ops.export_scene.fbx(
        filepath=out_path, use_selection=True, object_types={'ARMATURE', 'MESH'},
        add_leaf_bones=False, bake_anim=True, bake_anim_use_all_actions=True,
        bake_anim_use_nla_strips=False, path_mode='AUTO', embed_textures=False,
        mesh_smooth_type='FACE', use_mesh_modifiers=False, armature_nodetype='NULL')
    print(f'EXPORTED {out_path}')


main()
