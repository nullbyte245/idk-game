"""Normalises a Sketchfab prop/rig FBX for Unity: metre scale, stable material names,
colour maps written next to the model, and a printout of the geometry the mounts are computed from.

  blender -b --python prep_prop.py -- --src <fbx> --out <fbx> --textures <dir> \
            [--scale S] [--rename old=new,...]

Unity binds textures by material name (SceneBuilder.TexturedMaterial), so the rename happens here
rather than in the FBX's own material slots. Everything printed is measured after the scale is
baked in, which is what the mount numbers in SceneBuilder are read from.
"""
import os
import sys

from mathutils import Matrix, Vector

import bpy

COLOR_SKIP = ('normal', 'orm', 'ao', '_metal', 'rough')


def arg(name, default=None):
    return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default


def report(objs):
    for obj in objs:
        if obj.type == 'MESH':
            corners = [obj.matrix_world @ Vector(corner) for corner in obj.bound_box]
            center = sum(corners, Vector()) / len(corners)
            size = [max(c[i] for c in corners) - min(c[i] for c in corners) for i in range(3)]
            print(f'MESH {obj.name} size={tuple(round(v, 4) for v in size)} '
                  f'center={tuple(round(v, 4) for v in center)}')
        if obj.type == 'ARMATURE':
            print(f'ARMATURE {obj.name} world={tuple(round(v, 4) for v in obj.matrix_world.translation)}')
            for bone in obj.data.bones:
                head = obj.matrix_world @ bone.head_local
                tail = obj.matrix_world @ bone.tail_local
                axis = (tail - head)
                y = tuple(round(v, 4) for v in axis.normalized()) if axis.length > 1e-6 else ()
                print(f'  BONE {bone.name} parent={bone.parent.name if bone.parent else "-"} '
                      f'head={tuple(round(v, 4) for v in head)} '
                      f'len={axis.length:.4f} y={y}')


def scale_data(objs, factor):
    """Scale the vertex data and the bone rest positions directly, not the object transforms.

    transform_apply(scale) on a mesh parented to a scaled armature folds the parent's scale into
    the child as well, which leaves the skin about 40x smaller than the rig it deforms. Editing the
    data keeps mesh and skeleton in one piece, in metres, with every object transform untouched.
    """
    matrix = Matrix.Scale(factor, 4)
    for obj in objs:
        if obj.type == 'MESH':
            obj.data.transform(matrix)
            obj.data.update()
        elif obj.type == 'ARMATURE':
            bpy.context.view_layer.objects.active = obj
            bpy.ops.object.mode_set(mode='EDIT')
            for bone in obj.data.edit_bones:
                bone.head = matrix @ bone.head
                bone.tail = matrix @ bone.tail
            bpy.ops.object.mode_set(mode='OBJECT')
    bpy.context.view_layer.update()


def main():
    src, out, textures = arg('--src'), arg('--out'), arg('--textures')
    scale = float(arg('--scale', '1'))
    renames = dict(p.split('=', 1) for p in (arg('--rename', '') or '').split(',') if '=' in p)

    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=src)

    objs = [o for o in bpy.data.objects if o.type in {'MESH', 'ARMATURE'}]
    if not objs:
        raise SystemExit(f'IMPORT nothing found in {src}')

    if abs(scale - 1.0) > 1e-6:
        scale_data(objs, scale)
    print(f'SCALE factor={scale}')

    for mesh in (o for o in objs if o.type == 'MESH'):
        for i, material in enumerate(mesh.data.materials):
            if material is None:
                continue
            new = renames.get(material.name)
            if new:
                material.name = new
                mesh.data.materials[i] = material

    report(objs)

    saved = []
    for mesh in (o for o in objs if o.type == 'MESH'):
        for material in mesh.data.materials:
            if material is None or material.node_tree is None:
                continue
            for node in material.node_tree.nodes:
                if node.type != 'TEX_IMAGE' or node.image is None:
                    continue
                if any(token in node.image.name.lower() for token in COLOR_SKIP):
                    continue
                path = os.path.join(textures, material.name + '.png')
                try:
                    node.image.filepath_raw = path
                    node.image.file_format = 'PNG'
                    node.image.save()
                    saved.append(material.name)
                except RuntimeError as error:
                    # Sketchfab packs sometimes reference a texture the importer never resolved;
                    # the colour map is copied into place by hand instead.
                    print(f'TEXTURE SKIP {material.name}: {error}')
    print(f'TEXTURES saved={saved}')

    os.makedirs(os.path.dirname(out), exist_ok=True)
    bpy.ops.object.select_all(action='DESELECT')
    for obj in objs:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.export_scene.fbx(filepath=out, use_selection=True,
                             object_types={'ARMATURE', 'MESH'}, add_leaf_bones=False,
                             bake_anim=False, path_mode='AUTO', embed_textures=False,
                             mesh_smooth_type='FACE', use_mesh_modifiers=False,
                             armature_nodetype='NULL')
    print(f'EXPORTED {out}')


main()
