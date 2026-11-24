// The bandits' bodies (D129): brick-style BadCharacters people, survivors and raiders, without weapons (the game
// puts its own gun in their hands with the arm IK). Needs BadCharacters cloned beside BalticDrive, with `npm install`
// done there. Run from the BadCharacters folder:
//     npx tsx ../BalticDrive/tools/people/export_bandits.ts
// Writes Assets/People/Bandits/bandit_N.glb plus its .badchar.json recipe. Same seeds, same people.
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { generateCharacter } from '../../../BadCharacters/src/core/character';
import { randomRecipe } from '../../../BadCharacters/src/core/recipe';
import { exportGlb } from '../../../BadCharacters/src/export/gltf';

const out = join(__dirname, '../../Assets/People/Bandits');
mkdirSync(out, { recursive: true });
const jobs: [string, number][] = [
  ['survivor', 101], ['survivor', 102], ['survivor', 103], ['survivor', 104], ['survivor', 105],
  ['raider', 201], ['raider', 202], ['raider', 203],
];
jobs.forEach(([theme, seed], i) => {
  const rc = randomRecipe(seed, 'brick', theme);
  const ch = generateCharacter(rc);
  const glb = exportGlb(ch);
  const name = `bandit_${i + 1}`;
  writeFileSync(join(out, `${name}.glb`), glb);
  writeFileSync(join(out, `${name}.badchar.json`), JSON.stringify(rc, null, 2));
  console.log(name, theme, seed, rc.name, `${(glb.length / 1024).toFixed(0)} KB`, `${ch.stats.triangles} tris`);
});
