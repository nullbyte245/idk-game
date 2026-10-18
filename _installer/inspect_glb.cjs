// Prints the skin joint names and material/texture bindings for the SAS glb.
const fs = require('fs');
const buf = fs.readFileSync(process.argv[2]);
let offset = 12, json = null;
while (offset < buf.length) {
    const len = buf.readUInt32LE(offset), type = buf.readUInt32LE(offset + 4);
    if (type === 0x4e4f534a) json = JSON.parse(buf.subarray(offset + 8, offset + 8 + len).toString('utf8'));
    offset += 8 + len;
}
const nodes = json.nodes, skin = json.skins[0];
const name = (i) => nodes[i].name || `#${i}`;
console.log('ROOT:', name(skin.skeleton ?? skin.joints[0]));
console.log('JUNCTIONS (' + skin.joints.length + '):');
console.log(skin.joints.map(i => name(i)).join('\n'));

const mesh = json.meshes[0];
console.log('PRIMITIVES:');
mesh.primitives.forEach((p, i) => {
    const m = json.materials[p.material];
    const tex = (t) => t === undefined ? '-' : json.images[json.textures[t.index].source].name;
    console.log(`  [${i}] attrs=${Object.keys(p.attributes).join('+')} material=${m.name}`);
    console.log(`       base=${tex(m.pbrMetallicRoughness.baseColorTexture)} mr=${tex(m.pbrMetallicRoughness.metallicRoughnessTexture)} normal=${tex(m.normalTexture)}`);
});
console.log('ANIM COUNT:', json.animations.length, 'names sample:',
    [...new Set(json.animations.map(a => (a.name || '').split('_')[0]))].join(','));
