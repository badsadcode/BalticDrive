// The game's guns (D130): BadGun weapons, WW2 style (Tom: "WW2, WW1, maybe modern"; picked from a lineup of 18).
// Needs BadCharacters cloned beside BalticDrive (BadGun is its submodule at vendor/BadGun) with `npm install` done.
// Run from the BadCharacters folder:
//     npx tsx ../BalticDrive/tools/guns/export_guns.ts
// Writes Assets/Guns/<id>.glb plus its BadGun manifest <id>.json. Same recipes, same guns.
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { buildManifest, generate, toGLB } from '../../../BadCharacters/vendor/BadGun/src/index';

const out = join(__dirname, '../../Assets/Guns');
mkdirSync(out, { recursive: true });
const guns: [string, any][] = [
  ['rifle', { archetype: 'rifle', style: 'ww2', seed: 3, parts: { receiver: 'boltaction', magazine: 'internal' } }],   // slot "mosin"
  ['smg', { archetype: 'smg', style: 'ww2', seed: 1 }],                                                                // slot "ak"
  ['pistol', { archetype: 'pistol', style: 'ww2', seed: 2 }],                                                          // slot "pistol"
  ['pistol_b', { archetype: 'pistol', style: 'ww2', seed: 1 }],                                                        // other pistol looks (bandits)
  ['pistol_c', { archetype: 'pistol', style: 'ww1', seed: 1 }],
];
for (const [id, recipe] of guns) {
  const w = generate(recipe);
  const m = buildManifest(w);
  writeFileSync(join(out, `${id}.glb`), toGLB(w));
  writeFileSync(join(out, `${id}.json`), JSON.stringify(m, null, 1));
  console.log(id, m.name, `${(m as any).bounds.size.map((v: number) => v.toFixed(2)).join(' x ')} m`, (m as any).animations.map((a: any) => a.name).join(','));
}
