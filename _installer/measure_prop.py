"""Describes a static prop mesh well enough to mount it blind: which world axis is the barrel,
which end is the muzzle (the thinner end), and where the balance point sits along the barrel.

  blender -b --python measure_prop.py -- --file <fbx> --axis Y
"""
import sys

from mathutils import Vector

import bpy

FILE = sys.argv[sys.argv.index('--file') + 1]
AXIS = {'X': 0, 'Y': 1, 'Z': 2}[sys.argv[sys.argv.index('--axis') + 1] if '--axis' in sys.argv else 'Y']

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=FILE)

meshes = [o for o in bpy.data.objects if o.type == 'MESH']
for mesh in meshes:
    world = [mesh.matrix_world @ v.co for v in mesh.data.vertices]
    low = min(w[AXIS] for w in world)
    high = max(w[AXIS] for w in world)
    print(f'AX {mesh.name} span={high - low:.4f} low={low:.4f} high={high:.4f} '
          f'pivot={mesh.matrix_world.translation.z:.4f}')
    for label, end in (('low', low), ('high', high)):
        slice_ = [w for w in world if abs(w[AXIS] - end) < 0.04]
        if not slice_:
            continue
        other = [i for i in range(3) if i != AXIS]
        spread = [max(w[i] for w in slice_) - min(w[i] for w in slice_) for i in other]
        print(f'  END {label} verts={len(slice_)} cross={tuple(round(s, 4) for s in spread)} '
              f'center={[round(sum(w[i] for w in slice_) / len(slice_), 4) for i in other]}')
    balanced = sum(1 for w in world if w[AXIS] > 0) / len(world)
    print(f'  MASS +side fraction={balanced:.3f}')
