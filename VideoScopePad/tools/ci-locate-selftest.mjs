//
//  ci-locate-selftest.mjs
//
//  验证 .github/workflows/build-ipa.yml 里「定位 Xcode 工程」那一步的路径逻辑。
//
//  背景：最初的工作流把项目目录硬编码成 `VideoScopePad/`，结果只要仓库结构稍有不同，
//  CI 就直接报一句让人看不懂的：
//      An error occurred trying to start process '/bin/bash' with working directory
//      '.../VideoScopePad'. No such file or directory
//  现在改成在仓库里自己搜 VideoScopePad.xcodeproj，这个脚本就是用来验证那套搜索/换算规则的。
//
//  注意：Windows 沙箱里起不了 Git bash（MSYS2 创建命名管道被拦），所以这里用 JS 复刻
//  同一套规则（find -> dirname -> 换算成相对仓库根的路径），验证的是路径数学，
//  而不是 bash 语法本身。bash 语法用的都是 POSIX 标准原语。
//
//  用法：node tools/ci-locate-selftest.mjs
//

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..', '..');

const PROJECT_NAME = 'VideoScopePad.xcodeproj';
const MAX_DEPTH = 5;

/// 等价于：find "$WS" -maxdepth 5 -type d -name 'VideoScopePad.xcodeproj' | head -1
function findProjectDir(root) {
    const queue = [{ dir: root, depth: 0 }];

    while (queue.length > 0) {
        const { dir, depth } = queue.shift();
        if (depth > MAX_DEPTH) continue;

        let entries;
        try {
            entries = fs.readdirSync(dir, { withFileTypes: true });
        } catch {
            continue;
        }

        // 先判断当前目录里有没有工程（与 find 的遍历顺序无关，这里取确定性的排序）
        if (entries.some(e => e.name === PROJECT_NAME && e.isDirectory())) {
            return dir;
        }

        for (const entry of entries.sort((a, b) => a.name.localeCompare(b.name))) {
            if (!entry.isDirectory()) continue;
            if (entry.name === '.git') continue;
            queue.push({ dir: path.join(dir, entry.name), depth: depth + 1 });
        }
    }

    return null;
}

/// 等价于：DIR = dirname(PROJ); if DIR == WS then "." else "${DIR#$WS/}"
function relativeToRoot(root, dir) {
    if (dir === root) return '.';
    const prefix = root.endsWith(path.sep) ? root : root + path.sep;
    if (!dir.startsWith(prefix)) return null;
    return dir.slice(prefix.length).split(path.sep).join('/');
}

let failures = 0;

function check(label, root, expected) {
    const dir = findProjectDir(root);
    const rel = dir === null ? null : relativeToRoot(root, dir);

    if (expected === 'none') {
        if (rel === null) {
            console.log(`  ✓ ${label} → 找不到工程，CI 会明确报错退出（不再是难懂的 bash 报错）`);
            return;
        }
        console.log(`  ✗ ${label} → 预期找不到，实际 rel=${rel}`);
        failures += 1;
        return;
    }

    if (rel !== expected) {
        console.log(`  ✗ ${label} → 预期 rel=${expected}，实际 rel=${rel}`);
        failures += 1;
        return;
    }

    const projectExists = fs.existsSync(path.join(root, rel, PROJECT_NAME));
    const artifactPath = `${rel}/VideoScopePad-unsigned.ipa`;
    console.log(`  ✓ ${label} → rel=${rel}`);
    console.log(`      working-directory 下能找到工程: ${projectExists ? '是' : '否 ✗'}`);
    console.log(`      产物上传路径: ${artifactPath}`);
    if (!projectExists) failures += 1;
}

const mock = fs.mkdtempSync(path.join(os.tmpdir(), 'vsp-ci-selftest-'));

function mk(...parts) {
    const full = path.join(mock, ...parts);
    fs.mkdirSync(full, { recursive: true });
    return full;
}

console.log('用模拟目录验证「定位 Xcode 工程」的路径逻辑：\n');

// A) 标准结构（本仓库现在的样子）
mk('a', 'VideoScopePad', PROJECT_NAME);
mk('a', 'VideoScopePad', 'VideoScopePad', 'App');
check('A 标准结构  根/VideoScopePad/VideoScopePad.xcodeproj', path.join(mock, 'a'), 'VideoScopePad');

// B) 扁平结构：网页拖拽上传文件夹内容后最常见的形态
mk('b', PROJECT_NAME);
mk('b', 'VideoScopePad', 'App');
check('B 扁平结构  工程就在仓库根', path.join(mock, 'b'), '.');

// C) 只传了源码，没传工程文件
mk('c', 'App');
mk('c', 'Views');
check('C 缺工程文件  只有源码', path.join(mock, 'c'), 'none');

// D) 多套了一层目录
mk('d', 'iPad OS Software Waform', 'VideoScopePad', PROJECT_NAME);
check('D 多套一层  根/某目录/VideoScopePad/…xcodeproj', path.join(mock, 'd'), 'iPad OS Software Waform/VideoScopePad');

// E) 仓库基本是空的（只有工作流文件）——就是这次报错的情形
mk('e', '.github', 'workflows');
fs.writeFileSync(path.join(mock, 'e', '.github', 'workflows', 'build-ipa.yml'), 'name: x\n');
check('E 空仓库  只有 .github/workflows', path.join(mock, 'e'), 'none');

console.log('\n对真实仓库跑一遍：\n');
check('本仓库', repoRoot, 'VideoScopePad');

// 真实仓库里还要确认源码目录齐全，否则 CI 会在编译阶段炸
const sourceDir = path.join(repoRoot, 'VideoScopePad', 'VideoScopePad');
for (const sub of ['App', 'Capture', 'Model', 'Render', 'Views', 'Shaders', 'Resources']) {
    const ok = fs.existsSync(path.join(sourceDir, sub));
    console.log(`  ${ok ? '✓' : '✗'} 源码目录 ${sub}`);
    if (!ok) failures += 1;
}
for (const file of ['project.pbxproj', 'xcshareddata/xcschemes/VideoScopePad.xcscheme']) {
    const ok = fs.existsSync(path.join(repoRoot, 'VideoScopePad', PROJECT_NAME, file));
    console.log(`  ${ok ? '✓' : '✗'} 工程内 ${file}`);
    if (!ok) failures += 1;
}

fs.rmSync(mock, { recursive: true, force: true });

console.log('');
if (failures === 0) {
    console.log('全部通过：四种目录结构都能正确定位，两种情况会明确报错；本仓库结构可直接编译。');
} else {
    console.log(`有 ${failures} 项未通过。`);
    process.exit(1);
}
