import { createHash } from "node:crypto";
import { copyFile, mkdir, readFile, readdir, stat, writeFile } from "node:fs/promises";
import { dirname, join, relative } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const outputDirectory = join(scriptDirectory, "..", "Resources", "Raw", "wwwroot", "map", "vendor");
const mode = process.argv[2];

if (mode !== "--write" && mode !== "--verify") {
  throw new Error("Use --write to update committed assets or --verify to check them.");
}

const assets = [
  { packageName: "leaflet", source: "node_modules/leaflet/dist/leaflet.css", output: "leaflet/leaflet.css" },
  { packageName: "leaflet", source: "node_modules/leaflet/dist/leaflet.js", output: "leaflet/leaflet.js" },
  { packageName: "leaflet", source: "node_modules/leaflet/LICENSE", output: "leaflet/LICENSE.txt" },
  { packageName: "leaflet.markercluster", source: "node_modules/leaflet.markercluster/dist/MarkerCluster.css", output: "leaflet.markercluster/MarkerCluster.css" },
  { packageName: "leaflet.markercluster", source: "node_modules/leaflet.markercluster/dist/leaflet.markercluster.js", output: "leaflet.markercluster/leaflet.markercluster.js" },
  { packageName: "leaflet.markercluster", source: "node_modules/leaflet.markercluster/MIT-LICENCE.txt", output: "leaflet.markercluster/LICENSE.txt" }
];

const normalizePath = value => value.replaceAll("\\", "/");
const hash = contents => createHash("sha256").update(contents).digest("hex");

const packageVersions = new Map();
for (const packageName of new Set(assets.map(asset => asset.packageName))) {
  const packageJson = JSON.parse(await readFile(join(scriptDirectory, "node_modules", packageName, "package.json"), "utf8"));
  packageVersions.set(packageName, packageJson.version);
}

const manifestEntries = [];
for (const asset of assets) {
  const sourcePath = join(scriptDirectory, ...asset.source.split("/"));
  const outputPath = join(outputDirectory, ...asset.output.split("/"));
  const sourceContents = await readFile(sourcePath);

  if (mode === "--write") {
    await mkdir(dirname(outputPath), { recursive: true });
    await copyFile(sourcePath, outputPath);
  } else {
    const outputContents = await readFile(outputPath);
    if (!sourceContents.equals(outputContents)) {
      throw new Error(`${asset.output} differs from ${asset.source}. Run npm run sync.`);
    }
  }

  manifestEntries.push({
    package: asset.packageName,
    version: packageVersions.get(asset.packageName),
    source: asset.source.replace("node_modules/", ""),
    output: asset.output,
    sha256: hash(sourceContents)
  });
}

const manifest = `${JSON.stringify({ generatedBy: "npm run sync", assets: manifestEntries }, null, 2)}\n`;
const manifestPath = join(outputDirectory, "vendor-manifest.json");
if (mode === "--write") {
  await writeFile(manifestPath, manifest, "utf8");
} else if ((await readFile(manifestPath, "utf8")) !== manifest) {
  throw new Error("vendor-manifest.json is stale. Run npm run sync.");
}

const listFiles = async directory => {
  const entries = await readdir(directory);
  const files = [];
  for (const entry of entries) {
    const path = join(directory, entry);
    if ((await stat(path)).isDirectory()) {
      files.push(...await listFiles(path));
    } else {
      files.push(normalizePath(relative(outputDirectory, path)));
    }
  }
  return files;
};

const expectedFiles = [...assets.map(asset => asset.output), "vendor-manifest.json"].sort();
const actualFiles = (await listFiles(outputDirectory)).sort();
if (JSON.stringify(actualFiles) !== JSON.stringify(expectedFiles)) {
  throw new Error(`Unexpected generated asset set. Expected ${expectedFiles.join(", ")}; found ${actualFiles.join(", ")}.`);
}

console.log(mode === "--write" ? "Map vendor assets synchronized." : "Map vendor assets are reproducible.");
