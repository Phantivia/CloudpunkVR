"""Exports the seated Corti fufu (art/CortiDoll, FBX + textures) for the plugin's passenger-seat doll.

The game is Unity 2018 IL2CPP and cannot import FBX at runtime, so the doll goes in as a plain triangle mesh in its seated
rest pose (positions, normals, UVs; Unity axes: x right, y up, the doll facing -z) and two 2048 PNGs.

  blender --background --python export_fufu.py -- <CortiFufu.fbx> <textures dir> <out dir>

Output: fufu.mesh, fufu_base.png, fufu_normal.png. fufu.mesh is little-endian:
  'CFU1', uint32 vertex count, uint32 index count, float32 x 8 per vertex (pos 3, normal 3, uv 2), uint32 indices.
"""
import os
import struct
import sys

import bpy

args = sys.argv[sys.argv.index('--') + 1:]
fbx, texdir, out = args[0], args[1], args[2]
os.makedirs(out, exist_ok=True)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=fbx)
deps = bpy.context.evaluated_depsgraph_get()

verts = []
index = {}
tris = []
for o in [o for o in bpy.context.scene.objects if o.type == 'MESH']:
    eo = o.evaluated_get(deps)
    me = eo.to_mesh()
    me.calc_loop_triangles()
    uvl = me.uv_layers.active.data if me.uv_layers.active else None
    m = eo.matrix_world
    n3 = m.to_3x3().inverted().transposed()
    corner = getattr(me, 'corner_normals', None)
    if corner is None:
        me.calc_normals_split()
    for t in me.loop_triangles:
        tri = []
        for li in t.loops:
            vi = me.loops[li].vertex_index
            p = m @ me.vertices[vi].co
            nn = corner[li].vector if corner is not None else me.loops[li].normal
            n = (n3 @ nn).normalized()
            uv = uvl[li].uv if uvl else (0, 0)
            # Blender (x right, y back, z up, right-handed) -> Unity (x, z, y): y up, left-handed; the doll faces -z
            v = (round(p.x, 5), round(p.z, 5), round(p.y, 5), round(n.x, 3), round(n.z, 3), round(n.y, 3), round(uv[0], 5), round(uv[1], 5))
            k = index.get(v)
            if k is None:
                k = index[v] = len(verts)
                verts.append(v)
            tri.append(k)
        tris.extend(tri)
    eo.to_mesh_clear()

ys = [v[1] for v in verts]
xs = [v[0] for v in verts]
zs = [v[2] for v in verts]
h = max(ys) - min(ys)
k = 1.0
if h > 2:      # imported in centimetres
    k = 0.01
    verts = [(v[0] * k, v[1] * k, v[2] * k) + v[3:] for v in verts]
print(f'fufu: {len(verts)} vertices, {len(tris) // 3} triangles, height {h * k:.3f} m, x {min(xs) * k:.3f}..{max(xs) * k:.3f}, '
      f'y {min(ys) * k:.3f}..{max(ys) * k:.3f}, z {min(zs) * k:.3f}..{max(zs) * k:.3f}')

with open(os.path.join(out, 'fufu.mesh'), 'wb') as f:
    f.write(b'CFU1')
    f.write(struct.pack('<II', len(verts), len(tris)))
    for v in verts:
        f.write(struct.pack('<8f', *v))
    f.write(struct.pack(f'<{len(tris)}I', *tris))

scene = bpy.context.scene
scene.render.image_settings.file_format = 'PNG'
scene.render.image_settings.color_depth = '8'
for src, dst, mode in (('CortiFufu_BaseColor.png', 'fufu_base.png', 'RGBA'), ('CortiFufu_Normal.png', 'fufu_normal.png', 'RGB')):
    img = bpy.data.images.load(os.path.join(texdir, src))
    if src.endswith('Normal.png'):
        img.colorspace_settings.name = 'Non-Color'
    img.scale(2048, 2048)
    scene.render.image_settings.color_mode = mode
    img.save_render(os.path.join(out, dst), scene=scene)
    print(f'fufu: {dst} from {src}')
