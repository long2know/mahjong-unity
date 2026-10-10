import fs from "node:fs";
import path from "node:path";

const [projectArgument, outputArgument] = process.argv.slice(2);
if (!projectArgument || !outputArgument || process.argv.length !== 4) {
    throw new Error("Usage: node collect-npm-notices.mjs FRONTEND_PROJECT OUTPUT_DIRECTORY");
}
const project = path.resolve(projectArgument);
const output = path.resolve(outputArgument);
const lock = JSON.parse(fs.readFileSync(path.join(project, "package-lock.json"), "utf8"));
const components = [];
const inventory = [];
fs.mkdirSync(output, { recursive: true });
for (const [relative, declared] of Object.entries(lock.packages ?? {}).sort(([left], [right]) => left.localeCompare(right))) {
    if (!relative) continue;
    if (!relative.startsWith("node_modules/") || relative.split("/").some(part => !part || part === "." || part === "..")) {
        throw new Error(`Unsafe npm package path: ${relative}`);
    }
    const directory = path.join(project, relative);
    const metadataPath = path.join(directory, "package.json");
    const installed = fs.existsSync(metadataPath) && fs.lstatSync(metadataPath).isFile();
    const metadata = installed ? JSON.parse(fs.readFileSync(metadataPath, "utf8")) : declared;
    const name = metadata.name ?? relative.slice(relative.lastIndexOf("node_modules/") + "node_modules/".length);
    const version = metadata.version ?? declared.version;
    const license = metadata.license ?? declared.license ?? null;
    const notices = [];
    if (installed) {
        for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
            if (!entry.isFile() || !/^(?:licen[cs]e|copying|copyright|notice)(?:[._-].*)?$/i.test(entry.name)) continue;
            const destination = path.join(output, "notices", relative, entry.name);
            fs.mkdirSync(path.dirname(destination), { recursive: true });
            fs.copyFileSync(path.join(directory, entry.name), destination);
            notices.push(path.relative(output, destination).split(path.sep).join("/"));
        }
    }
    inventory.push({ name, version, declaredLicense: license, installed, notices });
    components.push({
        type: "library", name, ...(version ? { version } : {}),
        properties: [{ name: "npm:declared-license", value: JSON.stringify(license) }]
    });
}
fs.writeFileSync(path.join(output, "bom.cdx.json"), JSON.stringify({
    bomFormat: "CycloneDX", specVersion: "1.5", version: 1, components
}, null, 2) + "\n");
fs.writeFileSync(path.join(output, "inventory.json"), JSON.stringify({
    qualification: "Dependency declarations and verbatim notices, not a redistribution-license determination.",
    packages: inventory
}, null, 2) + "\n");
