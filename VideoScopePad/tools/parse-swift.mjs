//
//  parse-swift.mjs
//
//  在 Windows 上对全部 .swift 源文件做「真语法解析」预检 —— 不依赖 Xcode，
//  用 tree-sitter 的预编译 Swift 语法（wasm）逐文件建语法树，报告 ERROR / MISSING 节点。
//
//  用途：这台机器编译不了 iOS 工程，云端 CI 一次要跑好几分钟；
//  先在本地把纯语法错误挡掉，避免白白浪费 CI 轮次。
//
//  注意：它只做语法解析，不做类型检查 —— "找不到某个 API" 这类错误只能靠真编译发现。
//
//  首次使用需要装依赖（放在任意目录，用 VSP_TS_DIR 指定）。
//  注意版本必须配套：web-tree-sitter 0.27 载入不了 tree-sitter-wasms 里的语法 wasm，
//  实测可用组合是 web-tree-sitter@0.20.8 + tree-sitter-wasms@0.1.13：
//      set VSP_TS_DIR=%TEMP%\vsp-swift-parse
//      mkdir %VSP_TS_DIR% && cd /d %VSP_TS_DIR%
//      npm init -y && npm i web-tree-sitter@0.20.8 tree-sitter-wasms@0.1.13
//
//  用法：node tools/parse-swift.mjs
//
//  ⚠️ 已知现象：web-tree-sitter 0.20 的 emscripten 运行时在 Node 24 上退出时会崩一次
//  （Fatal process out of memory: Zone）。所有输出在崩溃前就已经完整打印，所以**以最后一行
//  「语法预检通过 / 发现 N 处可疑点」为准，不要看进程退出码**。
//

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoProjectDir = path.resolve(scriptDir, '..');      // VideoScopePad/
const sourceRoot = path.join(repoProjectDir, 'VideoScopePad'); // 源码目录

// MARK: - 定位解析器依赖

const moduleDirs = [
    process.env.VSP_TS_DIR,
    path.join(os.tmpdir(), 'vsp-swift-parse'),
    repoProjectDir
].filter(Boolean);

function resolveDeps() {
    for (const dir of moduleDirs) {
        const webTs = path.join(dir, 'node_modules', 'web-tree-sitter');
        const wasm = path.join(dir, 'node_modules', 'tree-sitter-wasms', 'out', 'tree-sitter-swift.wasm');
        if (fs.existsSync(webTs) && fs.existsSync(wasm)) {
            return { dir, webTs, wasm };
        }
    }
    return null;
}

const deps = resolveDeps();
if (!deps) {
    console.log('没有找到解析器依赖，跳过语法预检。');
    console.log('安装方式（任选目录，然后设环境变量 VSP_TS_DIR 指向它）：');
    console.log('  mkdir %TEMP%\\vsp-swift-parse && cd /d %TEMP%\\vsp-swift-parse');
    console.log('  npm init -y && npm i web-tree-sitter tree-sitter-wasms');
    console.log('  set VSP_TS_DIR=%TEMP%\\vsp-swift-parse');
    process.exit(0);
}

// MARK: - 载入 tree-sitter

const pkg = JSON.parse(fs.readFileSync(path.join(deps.webTs, 'package.json'), 'utf8'));

// web-tree-sitter 0.25+ 的 package.json 里 main/module 是空的，必须从 exports 映射里取
const exportEntry = pkg.exports?.['.'];
const entryCandidates = [
    typeof exportEntry === 'object'
        ? (exportEntry.import?.default ?? exportEntry.require?.default)
        : exportEntry,
    pkg.module,
    pkg.main,
    'web-tree-sitter.js',
    'tree-sitter.js',
    'index.js'
].filter(Boolean);

let loaded = null;

for (const candidate of entryCandidates) {
    const full = path.join(deps.webTs, candidate);
    if (!fs.existsSync(full)) continue;
    const mod = await import(pathToFileURL(full).href);
    const ns = mod.default ?? mod;
    const ParserClass = ns.Parser ?? (typeof ns === 'function' ? ns : null);
    if (ParserClass) {
        loaded = { ParserClass, ns };
        break;
    }
}

if (!loaded) {
    console.log('web-tree-sitter 载入失败：没找到 Parser 导出。');
    process.exit(1);
}

const { ParserClass, ns } = loaded;
const LanguageClass = ns.Language ?? ParserClass.Language;

await ParserClass.init();

// 直接读字节传给 Language.load，避免 0.27 里「字符串当路径还是当 URL」的歧义
const wasmBytes = fs.readFileSync(deps.wasm);
let language;
if (LanguageClass && typeof LanguageClass.load === 'function') {
    language = await LanguageClass.load(wasmBytes);
} else if (ParserClass.Language && typeof ParserClass.Language.load === 'function') {
    language = await ParserClass.Language.load(wasmBytes);
} else {
    console.log('无法加载 Swift 语法 wasm。');
    process.exit(1);
}

const parser = new ParserClass();
parser.setLanguage(language);

// MARK: - 收集源文件

function walk(dir, out = []) {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        if (entry.name.startsWith('.')) continue;
        const full = path.join(dir, entry.name);
        if (entry.isDirectory()) {
            if (full.endsWith('.xcassets')) continue;
            walk(full, out);
        } else if (entry.name.endsWith('.swift')) {
            out.push(full);
        }
    }
    return out;
}

const files = walk(sourceRoot).sort();

// MARK: - 解析

function collectIssues(node, out) {
    if (node.type === 'ERROR' || node.isMissing) {
        out.push(node);
    }
    for (const child of node.children) {
        collectIssues(child, out);
    }
}

// 0.20.x 里 hasError 是方法，0.22+ 是属性，两种都兼容
function nodeHasError(node) {
    const value = node.hasError;
    return typeof value === 'function' ? value.call(node) : Boolean(value);
}

let totalIssues = 0;
let filesWithIssues = 0;
let parsedOk = 0;

for (const file of files) {
    const source = fs.readFileSync(file, 'utf8');
    const tree = parser.parse(source);
    const rel = path.relative(repoProjectDir, file);

    if (!tree || !nodeHasError(tree.rootNode)) {
        console.log(`  ✓ ${rel}`);
        parsedOk += 1;
        tree?.delete?.();
        continue;
    }

    const issues = [];
    collectIssues(tree.rootNode, issues);
    filesWithIssues += 1;
    totalIssues += issues.length;

    console.log(`  ✗ ${rel}  （${issues.length} 处可疑）`);
    for (const node of issues.slice(0, 8)) {
        const line = node.startPosition.row + 1;
        const column = node.startPosition.column + 1;
        const snippet = source.slice(node.startIndex, Math.min(node.endIndex, node.startIndex + 100))
            .replace(/\s+/g, ' ')
            .trim();
        console.log(`      ${line}:${column}  ${node.isMissing ? 'MISSING ' + node.type : node.type}  →  ${snippet}`);
    }
    if (issues.length > 8) {
        console.log(`      …还有 ${issues.length - 8} 处`);
    }

    tree.delete?.();
}

console.log('');
if (totalIssues === 0) {
    console.log(`语法预检通过：${files.length} 个 Swift 文件全部解析成功（${parsedOk} 个无错），没有 ERROR / MISSING 节点。`);
    process.exit(0);
} else {
    console.log(`语法预检发现 ${totalIssues} 处可疑点，分布在 ${filesWithIssues} 个文件里。`);
    console.log('（tree-sitter 偶尔会对新语法误报，需要人工确认后再改）');
    process.exit(1);
}
