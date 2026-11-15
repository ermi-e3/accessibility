import { createHash } from "node:crypto";
import { copyFile, readdir, readFile, rm } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const outputDirectory = join(scriptDirectory, "..", "..", "accessibility.Api", "wwwroot", "sdk", "v1");
const stableFile = join(outputDirectory, "accesslearn-player.min.js");
const content = await readFile(stableFile);
const hash = createHash("sha256").update(content).digest("hex").slice(0, 12);

for (const file of await readdir(outputDirectory)) {
  if (/^accesslearn-player\.[0-9a-f]{12}\.js$/.test(file)) {
    await rm(join(outputDirectory, file));
  }
}

await copyFile(stableFile, join(outputDirectory, `accesslearn-player.${hash}.js`));
