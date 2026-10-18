"""Wraps the CSS hands around the AK: moves each wrist onto the part it should hold, turns the hand
so its knuckle line lies along that part, then curls the fingers around it and bakes the result as
the rest pose.

Everything is measured off geometry rather than eyeballed:

* the grip and handguard targets come from the gun mesh slices (grip_scan.py);
* the hand's frame - finger direction and knuckle line - comes from the joint positions, because
  this rig points each phalanx across its own chain, so bone.matrix would curl the fingers through
  the magazine and the mesh's rest-space vertices never move at all;
* the curl axis is that knuckle line, so a finger sweeps around the part it wraps instead of
  folding into the gun.

  blender -b --python grip_pose.py -- --arms _grip/CSSArms.fbx --gun _grip/AK47.fbx
                                      --out _grip/CSSArmsArmed.fbx --preview _grip/preview
"""
import math
import os
import sys

from mathutils import Euler, Matrix, Vector

import bpy

VIEW_POSE = {
    'bone': 'v_weapon',
    'offset': Vector((-0.1196, -0.2137, -0.0185)),
    'pitch': -4.0,
    'roll': 0.0,
    'yaw': 6.0,
    'scale': 1.0,
}

# Read off the gun mesh: the pistol grip hangs below the receiver around (-0.118, -0.228, -0.06),
# the handguard tube runs from y=-0.60 to y=-0.86 centred on z=+0.08. `side` is where the back of
# the hand sits relative to the part axis, so the knuckles land on the face the palm presses.
GRIP = {'center': Vector((-0.118, -0.228, -0.062)), 'axis': Vector((0, 0.71, 0.71)).normalized(),
        'side': Vector((-1, 0, 0)), 'along': 0.030, 'radius': 0.020}
# The support hand comes in under the tube: the palm faces up and across, wrapping around the far
# side, which is the only way a left hand reaches a handguard this narrow without breaking the wrist.
# Its centre is the rear handguard slice, not the middle of the tube: this rig has no upper arm, so
# the shoulder anchor to knuckle line is 29.4 cm at full extension, and aiming at the tube's centre
# 8 cm beyond that stretched the sleeve between Left_Arm and Hands_parent into a torn sheet.
FOREND = {'center': Vector((-0.071, -0.610, 0.080)), 'axis': Vector((0, 1, 0)),
          'side': Vector((0.45, 0.0, -0.89)).normalized(), 'along': -0.010, 'radius': 0.030}
# Knuckle flesh is not a mathematical surface: the joints sit a few millimetres clear of the metal.
SKIN = 0.008
# How far the pad of a fingertip sits past its last joint.
FLESH_TIP = 0.014

# Where the trigger blade sits, measured off the guard: the loop spans y=-0.30 (front wall) to
# y=-0.21 (rear) at x=-0.096..-0.104, so the pad of the index lands just inside its rear face.
TRIGGER = Vector((-0.100, -0.250, 0.005))

# Where the forearm points from the wrist: back and down, which is where a rifle elbow lives. The
# elbow is off the viewmodel frame, so it is placed rather than solved.
RIGHT_ELBOW = Vector((-0.15, 0.85, -0.50))
LEFT_ELBOW = Vector((0.20, 0.85, -0.49))

# Per-joint turn about the knuckle line. The wrapping fingers close around the part; the index is
# left out of the right hand's handful and solved separately onto the trigger, so it is listed only
# for the support hand, where it wraps like the rest.
CURL = {
    'Right': {'Index': (30, 34, 22), 'Middle': (46, 44, 28), 'Ring': (46, 44, 28),
              'Pinky': (42, 42, 26), 'Thumb': (34, 28, 18)},
    'Left': {'Index': (34, 36, 24), 'Middle': (48, 46, 30), 'Ring': (48, 46, 30),
             'Pinky': (46, 44, 26), 'Thumb': (40, 28, 16)},
}
FINGERS = ('Index', 'Middle', 'Ring', 'Pinky', 'Thumb')

# The index is solved, not hand-tuned: a relaxed trigger finger is nearly straight and reaches well
# past the curled ones, so the bend and the sideways swing are searched against the blade position.
INDEX_BENDS = ((0, 0), (10, 6), (20, 12), (30, 18), (40, 24))
INDEX_LIFTS = tuple(range(-45, 50, 5))
# How far the whole hand may climb toward the receiver, and how badly drifting off the grip counts
# against a hit on the trigger. A wrap lost is obvious at a glance; a trigger a centimetre short is
# not, so the wrap is weighted above the miss.
INDEX_CLIMBS = tuple(round(v, 3) for v in [i * 0.01 for i in range(6)])
WRAP_WEIGHT = 2.0


def arg(name):
    return sys.argv[sys.argv.index(name) + 1]


def bone(arm, side, part):
    """Every phalanx is spelled tightly except the left thumb's middle one, which the source file
    names Left_Thumb_02, so both spellings have to be tried before a wrap loses a joint."""
    for name in (f'v_weapon.{side}_{part}', f'v_weapon.{side}_{part[:-2]}_{part[-2:]}'):
        found = arm.pose.bones.get(name)
        if found is not None:
            return found
    return None


def build():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=os.path.abspath(arg('--arms')))
    arm = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
    meshes = [o for o in bpy.data.objects if o.type == 'MESH' and o.parent == arm]
    if arm.animation_data:
        arm.animation_data.action = None
        for track in list(arm.animation_data.nla_tracks):
            arm.animation_data.nla_tracks.remove(track)
    arm.data.pose_position = 'POSE'

    bpy.ops.import_scene.fbx(filepath=os.path.abspath(arg('--gun')))
    known = {m.name for m in meshes} | {arm.name}
    gun = next(o for o in bpy.data.objects if o.type == 'MESH' and o.name not in known)
    print(f'GUN {gun.name} dims={tuple(round(v, 3) for v in gun.dimensions)} '
          f'verts={len(gun.data.vertices)}')

    # No rest-pose baking here. Applying a pose as rest leaves the skin at its original rest
    # coordinates while the bones move, so the left hand's bones ended up 20 cm away from the left
    # hand's mesh and every wrap measured after that was a lie about the render.
    mount(gun, arm, VIEW_POSE)
    return arm, meshes, gun


def mount(obj, armature, pose):
    head = armature.matrix_world @ armature.pose.bones[pose['bone']].matrix.translation
    extra = Euler((math.radians(pose['pitch']), math.radians(pose['roll']),
                   math.radians(pose['yaw'])), 'XYZ').to_matrix().to_4x4()
    base = obj.matrix_world.to_quaternion().to_matrix().to_4x4()
    scale = obj.matrix_world.to_scale() * pose['scale']
    world = Matrix.Translation(head + pose['offset']) @ extra @ base @ Matrix.Diagonal(scale.to_4d())
    obj.parent = armature
    obj.parent_type = 'BONE'
    obj.parent_bone = pose['bone']
    obj.matrix_parent_inverse = Matrix.Identity(4)
    obj.matrix_basis = Matrix.Identity(4)
    bpy.context.view_layer.update()
    obj.matrix_basis = obj.matrix_world.inverted() @ world
    bpy.context.view_layer.update()


def freeze(meshes):
    """Bake the wrap into the skin rather than into the skeleton. FirstPersonViewModel only ever moves
    the viewmodel root and never poses a bone, so a skinned mesh exported against a rest-pose skeleton
    comes back on screen as the CSS idle - and baking the pose as rest instead returns the skin to its
    original coordinates while the bones walk away, which is worse. Applying the armature puts the
    wrapped shape into the vertices themselves; the bones then sit at rest and only carry the gun's
    parent."""
    depsgraph = bpy.context.evaluated_depsgraph_get()
    for mesh in meshes:
        evaluated = mesh.evaluated_get(depsgraph)
        before = [mesh.matrix_world @ vertex.co for vertex in evaluated.data.vertices]
        posed = bpy.data.meshes.new_from_object(evaluated)
        old = mesh.data
        if not posed.materials:
            for slot in old.materials:
                posed.materials.append(slot)
        mesh.modifiers.clear()
        mesh.data = posed
        for group in list(mesh.vertex_groups):
            mesh.vertex_groups.remove(group)
        evaluated.to_mesh_clear()
        drift = max((a - b).length
                    for a, b in zip(before, [vertex.co for vertex in posed.vertices]))
        print(f'FREEZE {mesh.name} verts={len(posed.vertices)} '
              f'mats={[m.name if m else None for m in posed.materials]} '
              f'drift={round(drift * 1000, 2)}mm')
        bpy.data.meshes.remove(old)


def to_unity_axes(objects):
    """Turn the authored view space into Unity's, where it is the vertices that have to move.

    This file is built in a Z-up frame: the rifle runs along -Y and +Z is up. Unity reads the mesh
    data of this prefab straight through - the node chain above the hands carries a scale and nothing
    else - so an unrotated export arrives with a 0.95 m long box standing on end, the gun pointing at
    the floor and both hands hanging under the eye, out of the viewmodel camera's frustum.

    The rotation cannot go on the armature: turning that node moves the bones and the bone-parented
    gun, while Unity re-parents the plain meshes under the prefab root and drops the node's rotation
    with it, so skin and skeleton would drift apart. Written into each object's own vertices as a
    conjugated world rotation, the whole assembly turns rigidly about the view origin and every node
    transform stays exactly where the importer wants it.
    """
    turn = Matrix.Rotation(math.radians(-90.0), 4, 'X')
    for obj in objects:
        world = obj.matrix_world
        to_local = world.inverted() @ turn @ world
        for vertex in obj.data.vertices:
            vertex.co = to_local @ vertex.co
        obj.data.update()
        stride = max(1, len(obj.data.vertices) // 512)
        corners = [obj.matrix_world @ obj.data.vertices[i].co
                   for i in range(0, len(obj.data.vertices), stride)]
        span = [max(axis) - min(axis) for axis in zip(*corners)]
        centre = sum(corners, Vector()) / len(corners)
        print(f'AXES {obj.name} worldBox={tuple(round(v, 3) for v in span)} '
              f'centre={tuple(round(v, 3) for v in centre)}')


def world_head(arm, pb):
    return arm.matrix_world @ pb.matrix.translation


def write(arm, pb, world_move):
    """Apply a world-space rigid motion to one pose bone."""
    pb.matrix = arm.matrix_world.inverted() @ world_move @ arm.matrix_world @ pb.matrix
    bpy.context.view_layer.update()


def translate(arm, pb, offset):
    if Vector(offset).length < 1e-6:
        return
    write(arm, pb, Matrix.Translation(Vector(offset)))


def rotate_about(arm, pb, pivot, axis, degrees):
    if abs(degrees) < 1e-4:
        return
    pivot = Vector(pivot)
    move = (Matrix.Translation(pivot) @ Matrix.Rotation(math.radians(degrees), 4, Vector(axis))
            @ Matrix.Translation(-pivot))
    write(arm, pb, move)


def joint_head(arm, side, part):
    pb = bone(arm, side, part)
    return None if pb is None else world_head(arm, pb)


def snapshot(arm):
    return {name: pb.matrix.copy() for name, pb in arm.pose.bones.items()}


def fingertip(arm, side, finger):
    """The last joint is not the end of the finger. Its flesh sits a centimetre and a half past the
    joint, so scoring the joint alone read a fingertip that visibly touches the metal as a miss."""
    last = bone(arm, side, f'{finger}03')
    prior = bone(arm, side, f'{finger}02')
    head = world_head(arm, last)
    span = head - world_head(arm, prior)
    return head + (span.normalized() if span.length > 1e-6 else Vector()) * FLESH_TIP


_ORDERS = {}


def order_first(arm):
    """Parent-before-child bone names, from the rest hierarchy. Neither collection is documented to
    hand back a topological order, and a restore that lands a child before its parent is back solves
    it against the wrong parent - the child then drifts when the parent finally moves."""
    order = _ORDERS.get(arm.name)
    if order is not None:
        return order
    pending = {rest.name for rest in arm.data.bones if rest.name in arm.pose.bones}
    order = []
    while pending:
        ready = [name for name in pending
                 if arm.data.bones[name].parent is None
                 or arm.data.bones[name].parent.name not in pending]
        if not ready:
            order.extend(sorted(pending))
            break
        order.extend(sorted(ready))
        pending.difference_update(ready)
    _ORDERS[arm.name] = order
    return order


def restore(arm, state):
    """Replay a snapshot bone by bone, parent first, updating after every single write. The matrix
    setter has to divide out the parent's transform to recover the local rotation it stores, so if
    the parent was just written and the depsgraph still reports the old one, the child is stored
    against a stale parent and lands somewhere else entirely as soon as anything updates. That is
    what made a restored pose score differently from the identical pose that produced it: the worst
    drift was 24 cm, all of it on the deepest finger chains."""
    for name in order_first(arm):
        pose_bone = arm.pose.bones.get(name)
        if pose_bone is not None and name in state:
            pose_bone.matrix = state[name]
            bpy.context.view_layer.update()


def aim_index(arm, side, line, target, bends, lifts, quiet=False):
    """Put the fingertip on the trigger. One swing around the knuckle line is not enough: that only
    moves the tip across the grip, while the blade is also 6 cm farther forward, so the first
    segment is rotated to point at the metal directly and the remaining two are then bent to bring
    the tip back onto it. The lift is searched because a finger that reaches the trigger while
    staying straight looks stiff, and the bend that makes it look relaxed pulls the tip off target
    unless the aim is raised to compensate."""
    state = snapshot(arm)

    def apply(bend, lift):
        restore(arm, state)
        knuckle = bone(arm, side, 'Index01')
        second = bone(arm, side, 'Index02')
        pivot = world_head(arm, knuckle)
        want = Vector(target) - pivot
        if lift:
            want = Matrix.Rotation(math.radians(lift), 3, Vector(line)) @ want
        have = world_head(arm, second) - pivot
        if have.length < 1e-6 or want.length < 1e-6:
            return
        spin = have.normalized().rotation_difference(want.normalized()).to_matrix().to_4x4()
        write(arm, knuckle, Matrix.Translation(pivot) @ spin @ Matrix.Translation(-pivot))
        for joint, degrees in zip(('02', '03'), bend):
            pb = bone(arm, side, f'Index{joint}')
            rotate_about(arm, pb, world_head(arm, pb), line, -degrees)

    best = None
    for bend in bends:
        for lift in lifts:
            apply(bend, lift)
            gap = (fingertip(arm, side, 'Index') - Vector(target)).length
            if best is None or gap < best[0]:
                best = (gap, bend, lift)
    _, bend, lift = best
    apply(bend, lift)
    tip = fingertip(arm, side, 'Index')
    replayed = (tip - Vector(target)).length
    if quiet:
        return
    print(f'INDEX {side} -> trigger bend={bend} lift={lift} '
          f'tip={tuple(round(v, 3) for v in tip)} gap={round(replayed * 100, 1)}cm')


def hand_frame(arm, side):
    """Finger direction and knuckle line from the joint positions. Not from the skin: a mesh object's
    vertices are rest-space, so weighting them would report the idle pose no matter how the bones
    are turned. Not from the bone axes either - this rig points each phalanx across its own chain."""
    heads = [joint_head(arm, side, f'{finger}01') for finger in FINGERS[:4]]
    tips = [joint_head(arm, side, f'{finger}03') for finger in FINGERS[:4]]
    if any(h is None for h in heads) or any(t is None for t in tips):
        return None, None
    line = (Vector(heads[3]) - Vector(heads[0])).normalized()
    direction = sum(((t - h).normalized() for h, t in zip(heads, tips)), Vector()).normalized()
    return direction, line


def frame_matrix(F, K):
    if F is None or K is None:
        return None
    F = Vector(F).normalized()
    K = Vector(K).normalized()
    N = F.cross(K)
    if N.length < 1e-4:
        return None
    F = K.cross(N).normalized()
    N = F.cross(K).normalized()
    return Matrix(((F.x, K.x, N.x, 0.0), (F.y, K.y, N.y, 0.0), (F.z, K.z, N.z, 0.0),
                   (0.0, 0.0, 0.0, 1.0)))


def orient(arm, side, pb, F_to, K_to):
    """Turn the hand until its measured frame matches the target. The current frame is read here,
    not passed in: the elbow swing that places the forearm happens before this call and rotates the
    hand with it, so a frame measured at the start of the pose is already stale by then."""
    F_now, K_now = hand_frame(arm, side)
    now = frame_matrix(F_now, K_now)
    to = frame_matrix(F_to, K_to)
    if now is None or to is None:
        print(f'ORIENT skipped on {pb.name}')
        return
    pivot = world_head(arm, pb)
    spin = to @ now.inverted()
    move = Matrix.Translation(pivot) @ spin @ Matrix.Translation(-pivot)
    write(arm, pb, move)


def swing(arm, pb, wrist, direction):
    """Turn the forearm about its own head so the wrist swings onto the line the elbow wants. The
    head is the anchor: this rig has no upper arm, so its head sits where the sleeve meets the body
    and any pivot further down the chain drags it off that anchor and stretches the skin into a
    sheet. Rotating here keeps the bone's 21.8 cm length, which is the arm's entire reach."""
    wrist = Vector(wrist)
    head = world_head(arm, pb)
    from_v = wrist - head
    if from_v.length < 1e-5:
        return
    to_v = -Vector(direction).normalized() * from_v.length
    spin = from_v.rotation_difference(to_v).to_matrix().to_4x4()
    write(arm, pb, Matrix.Translation(head) @ spin @ Matrix.Translation(-head))


def pose_hand(arm, side, target, elbow_dir, metal, trigger=None):
    hand = arm.pose.bones[f'v_weapon.{side}_Hand']
    fore = arm.pose.bones[f'v_weapon.{side}_Arm']
    middle = arm.pose.bones[f'v_weapon.{side}_Middle01']
    axis = Vector(target['axis']).normalized()
    side_dir = Vector(target['side']).normalized()
    # The knuckles, not the wrist, are what has to land on the part: the palm's anatomy then decides
    # where the wrist ends up, and driving the wrist to the surface pushed the fingers out and away.
    knuckles_on = (Vector(target['center']) + axis * target['along']
                   + side_dir * (target['radius'] + SKIN))

    F_now, K_now = hand_frame(arm, side)
    if F_now is None:
        print(f'{side.upper()} missing joints; pose skipped')
        return
    # Knuckles line up with the part axis, the palm presses the part, the fingers leave tangentially
    # and curl around it.
    K_to = -axis
    N_to = -side_dir
    F_to = K_to.cross(N_to)
    print(f'{side.upper()} frame now F={tuple(round(v, 2) for v in F_now)} '
          f'K={tuple(round(v, 2) for v in K_now)} | to F={tuple(round(v, 2) for v in F_to)} '
          f'K={tuple(round(v, 2) for v in K_to)}')

    # Three rigid moves, in that order: the forearm swings from its own shoulder end, the hand then
    # turns about the wrist that swing just placed, and the arm chain finally slides so the knuckles
    # touch the part. Each one is a rotation about a bone head or a translation of a whole chain, so
    # nothing between the shoulder and the fingertips ever stretches - and the slide only moves the
    # sleeve root, which sits off the edge of the viewmodel frame.
    swing(arm, fore, world_head(arm, hand), elbow_dir)
    orient(arm, side, hand, F_to, K_to)
    translate(arm, fore, Vector(knuckles_on) - world_head(arm, middle))
    print(f'{side.upper()} wrist={tuple(round(v, 3) for v in world_head(arm, hand))} '
          f'knuckles={tuple(round(v, 3) for v in world_head(arm, middle))} '
          f'want={tuple(round(v, 3) for v in knuckles_on)} '
          f'shoulder={tuple(round(v, 3) for v in world_head(arm, fore))} '
          f'reach={round((world_head(arm, fore) - world_head(arm, hand)).length * 100, 1)}cm')

    _, line = hand_frame(arm, side)
    print(f'{side.upper()} curl axis={tuple(round(v, 2) for v in line)}')
    for finger in FINGERS:
        if finger == 'Index' and trigger is not None:
            continue
        for joint, degrees in zip(('01', '02', '03'), CURL[side][finger]):
            pb = bone(arm, side, f'{finger}{joint}')
            if pb is None:
                print(f'  MISSING {side}_{finger}{joint}')
                continue
            rotate_about(arm, pb, world_head(arm, pb), line, -degrees)

    if trigger is None:
        return
    # The index has to reach a specific piece of hardware, so where it lands is solved rather than
    # tuned: the hand climbs toward the receiver a little at a time, the finger is aimed at the blade
    # for each climb, and the score weighs the miss on the trigger against how far the three
    # wrapping fingertips drift off the grip. Climbing is not free - the palm slides off the part it
    # is supposed to enclose - which is why the two are searched together instead of in turn.
    state = snapshot(arm)
    climb = (Vector(trigger) - Vector(target['center'])).normalized()
    best = None
    for slide in INDEX_CLIMBS:
        restore(arm, state)
        translate(arm, fore, climb * slide)
        aim_index(arm, side, line, trigger, INDEX_BENDS, INDEX_LIFTS, quiet=True)
        gap, wrap = wrap_score(arm, side, metal, trigger)
        score = gap + WRAP_WEIGHT * wrap
        print(f'  climb {slide * 100:4.1f}cm trigger-miss={gap * 100:4.1f}cm '
              f'wrap-off={wrap * 100:4.1f}cm score={score * 100:4.1f}')
        if best is None or score < best[0]:
            best = (score, slide)
    restore(arm, state)
    translate(arm, fore, climb * best[1])
    aim_index(arm, side, line, trigger, INDEX_BENDS, INDEX_LIFTS)
    print(f'{side.upper()} climbed {round(best[1] * 100, 1)}cm toward the trigger')


def gun_surface(gun):
    """Every vertex of the gun in world space, so a fingertip can be scored against real metal rather
    than against an idealised cylinder fitted to a slice."""
    import mathutils

    tree = mathutils.kdtree.KDTree(len(gun.data.vertices))
    for index, vertex in enumerate(gun.data.vertices):
        tree.insert(gun.matrix_world @ vertex.co, index)
    tree.balance()
    return tree


def wrap_score(arm, side, metal, trigger):
    """How far the index misses the blade, and how far the three wrapping fingertips have drifted off
    the grip. Both in metres, both measured on the flesh rather than on the joints."""
    gap = (fingertip(arm, side, 'Index') - Vector(trigger)).length
    off = [metal.find(fingertip(arm, side, finger))[2]
           for finger in ('Middle', 'Ring', 'Pinky')]
    return gap, sum(off) / len(off)


def main():
    arm, meshes, gun = build()
    print(f'ARM {arm.name} meshes={[m.name for m in meshes]}')
    metal = gun_surface(gun)

    pose_hand(arm, 'Right', GRIP, RIGHT_ELBOW, metal, trigger=TRIGGER)
    pose_hand(arm, 'Left', FOREND, LEFT_ELBOW, metal)
    bpy.context.view_layer.update()

    print('--- wrap check: each joint against the nearest gun surface ---')
    tip = fingertip(arm, 'Right', 'Index')
    print(f'  index tip to trigger {round((tip - TRIGGER).length * 100, 1)}cm '
          f'tip={tuple(round(v, 3) for v in tip)}')
    for side in ('Right', 'Left'):
        for finger in FINGERS:
            for joint in ('01', '02', '03'):
                pb = bone(arm, side, f'{finger}{joint}')
                if pb is None:
                    print(f'  MISSING {side}_{finger}{joint}')
                    continue
                point = world_head(arm, pb)
                co, _, dist = metal.find(point)
                print(f'  {side[0]}{finger[:2]}{joint} d={dist * 100:5.1f}cm '
                      f'joint={tuple(round(v, 3) for v in point)} '
                      f'metal={tuple(round(v, 3) for v in co)}')

    preview = os.path.abspath(arg('--preview'))
    os.makedirs(preview, exist_ok=True)
    bpy.context.scene.render.engine = 'BLENDER_WORKBENCH'
    bpy.context.scene.display.shading.light = 'STUDIO'
    bpy.context.scene.display.shading.color_type = 'MATERIAL'
    bpy.context.scene.render.resolution_x = 900
    bpy.context.scene.render.resolution_y = 700

    shots = {
        'posed_right': (GRIP['center'] + Vector((-0.8, -0.4, -0.45)).normalized() * 0.42, GRIP['center']),
        'posed_right_out': (GRIP['center'] + Vector((0.8, -0.3, 0.4)).normalized() * 0.45, GRIP['center']),
        'posed_left': (FOREND['center'] + Vector((0.75, 0.35, 0.5)).normalized() * 0.5,
                       FOREND['center']),
        'posed_eye': (Vector((0.02, 0.10, 0.06)), Vector((0.02, -0.60, -0.02))),
        # From behind and to the shooter's right, which is the only angle that shows whether the
        # index actually enters the guard or merely rests on the wall beside it.
        'posed_trigger': (Vector((-0.42, 0.10, 0.22)), TRIGGER),
        # Far enough back to catch the sleeve roots: that is where a stretched arm shows up as a
        # torn sheet, and every close-up crop hides it off the edge of the frame.
        'posed_wide': (Vector((0.42, 0.42, 0.30)), Vector((-0.02, -0.42, -0.02))),
    }
    for name, (location, target) in shots.items():
        cam = bpy.data.cameras.new(name)
        cam.lens = 35 if name in ('posed_eye', 'posed_wide') else 50
        cam.clip_end = 60.0
        obj = bpy.data.objects.new(name, cam)
        obj.location = location
        obj.rotation_euler = (Vector(target) - Vector(location)).to_track_quat('-Z', 'Y').to_euler()
        bpy.context.scene.collection.objects.link(obj)
        bpy.context.scene.camera = obj
        bpy.context.view_layer.update()
        bpy.context.scene.render.filepath = os.path.join(preview, name + '.png')
        bpy.ops.render.render(write_still=True)
        print(f'RENDER {name} loc={tuple(round(v, 3) for v in location)}')

    if '--out' in sys.argv:
        bpy.context.view_layer.update()
        freeze(meshes)
        to_unity_axes(meshes + [gun])
        corners = [gun.matrix_world @ gun.data.vertices[i].co
                   for i in range(0, len(gun.data.vertices), 512)]
        span = [max(axis) - min(axis) for axis in zip(*corners)]
        centre = sum(corners, Vector()) / len(corners)
        print(f'EXPORT GUN worldBox={tuple(round(v, 3) for v in span)} '
              f'centre={tuple(round(v, 3) for v in centre)}')
        # Keep the names the previous export used, so the scene's prefab instance resolves the same
        # hierarchy and only the geometry underneath it changes.
        gun.name = 'AK47_ViewModel'
        out = os.path.abspath(arg('--out'))
        bpy.ops.object.select_all(action='DESELECT')
        for obj in [arm] + meshes + [gun]:
            obj.select_set(True)
        bpy.context.view_layer.objects.active = arm
        os.makedirs(os.path.dirname(out), exist_ok=True)
        bpy.ops.export_scene.fbx(filepath=out, use_selection=True,
                                 object_types={'ARMATURE', 'MESH'}, add_leaf_bones=False,
                                 bake_anim=False, path_mode='AUTO', embed_textures=False,
                                 mesh_smooth_type='FACE', use_mesh_modifiers=False,
                                 armature_nodetype='NULL')
        print(f'EXPORTED {out}')


main()
