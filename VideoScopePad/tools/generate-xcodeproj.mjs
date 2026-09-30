//
//  generate-xcodeproj.mjs
//  VideoScopePad
//
//  生成 / 重新生成 VideoScopePad.xcodeproj（纯文本 project.pbxproj + 共享 scheme）。
//  这样在 Windows 上也能维护 Xcode 工程：新增/删除源文件后重新跑一次即可。
//
//  用法（仓库根目录下执行）：
//      node tools/generate-xcodeproj.mjs
//

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const rootDir = path.resolve(scriptDir, '..');

const projectName = 'VideoScopePad';
const sourcesDirName = 'VideoScopePad';
const sourcesAbs = path.join(rootDir, sourcesDirName);

// 应用内「设置 → 关于」显示的版本号（MARKETING_VERSION）。
// 每次发版改这一处，重新生成工程即可，不用手动动 pbxproj。
const appVersion = '1.9.0';

if (!fs.existsSync(sourcesAbs)) {
    console.error(`找不到源码目录: ${sourcesAbs}`);
    process.exit(1);
}

// MARK: - ID 生成

let idCounter = 0;
function nextId() {
    idCounter += 1;
    return 'AB' + idCounter.toString(16).toUpperCase().padStart(22, '0');
}

// MARK: - 扫描源码

const SOURCE_EXT = new Set(['.swift', '.metal']);
const RESOURCE_EXT = new Set(['.xcassets']);
const HEADER_EXT = new Set(['.h']);

function scan(dir) {
    const entries = fs.readdirSync(dir, { withFileTypes: true })
        .filter(e => !e.name.startsWith('.'))
        .sort((a, b) => {
            if (a.isDirectory() !== b.isDirectory()) return a.isDirectory() ? -1 : 1;
            return a.name.localeCompare(b.name);
        });

    const files = [];
    const dirs = [];

    for (const entry of entries) {
        const abs = path.join(dir, entry.name);
        if (entry.isDirectory()) {
            if (entry.name.endsWith('.xcassets')) {
                files.push({ name: entry.name, abs, ext: '.xcassets', isDir: true });
            } else {
                dirs.push({ name: entry.name, abs, tree: scan(abs) });
            }
        } else {
            const ext = path.extname(entry.name).toLowerCase();
            if (SOURCE_EXT.has(ext) || RESOURCE_EXT.has(ext) || HEADER_EXT.has(ext)) {
                files.push({ name: entry.name, abs, ext, isDir: false });
            }
        }
    }

    return { files, dirs };
}

const tree = scan(sourcesAbs);

// MARK: - 收集需要写入的条目

const buildFiles = [];   // { id, fileRefId, name, phase }
const fileRefs = [];     // { id, name, path, type, sourceTree }
const groups = [];       // { id, name, path, children: [ids], sourceTree }

function fileType(ext) {
    switch (ext) {
        case '.swift': return 'sourcecode.swift';
        case '.metal': return 'sourcecode.metal';
        case '.h': return 'sourcecode.c.h';
        case '.xcassets': return 'folder.assetcatalog';
        default: return 'text';
    }
}

function makeFileRef(node, groupPath) {
    const id = nextId();
    fileRefs.push({
        id,
        name: node.name,
        path: node.name,
        type: fileType(node.ext),
        sourceTree: '"<group>"'
    });
    return id;
}

function makeBuildFile(fileRefId, node) {
    const id = nextId();
    const phase = SOURCE_EXT.has(node.ext) ? 'Sources' : 'Resources';
    buildFiles.push({ id, fileRefId, name: node.name, phase });
    return id;
}

// 递归构建 group 树
function buildGroups(tree, name, relPath) {
    const groupId = nextId();
    const children = [];

    for (const file of tree.files) {
        const refId = makeFileRef(file, relPath);
        children.push(refId);
        if (SOURCE_EXT.has(file.ext) || RESOURCE_EXT.has(file.ext)) {
            makeBuildFile(refId, file);
        }
    }

    for (const dir of tree.dirs) {
        children.push(buildGroups(dir.tree, dir.name, path.posix.join(relPath, dir.name)));
    }

    groups.push({
        id: groupId,
        name,
        path: relPath === '' ? sourcesDirName : name,
        children,
        sourceTree: '"<group>"'
    });

    return groupId;
}

const mainSourceGroupId = buildGroups(tree, sourcesDirName, '');

// MARK: - 工程级对象 ID

const projectId = nextId();
const targetId = nextId();
const productRefId = nextId();
const mainGroupId = nextId();
const productsGroupId = nextId();
const sourcesBuildPhaseId = nextId();
const resourcesBuildPhaseId = nextId();
const frameworksBuildPhaseId = nextId();
const projectConfigListId = nextId();
const targetConfigListId = nextId();
const projectDebugConfigId = nextId();
const projectReleaseConfigId = nextId();
const targetDebugConfigId = nextId();
const targetReleaseConfigId = nextId();

// MARK: - SPM 依赖（HaishinKit：RTMP + SRT）

// HaishinKit 2.x 把协议拆成了独立 product，所以三个都要挂到 target 上：
//   HaishinKit      核心（MediaMixer / 编解码设置 / Session 协议）
//   RTMPHaishinKit  RTMP（握手 + AMF + FLV）
//   SRTHaishinKit   SRT（自带 libsrt 的 xcframework 二进制依赖）
// 解析包需要联网；CI 的 macOS runner 能直连 GitHub。
const swiftPackage = {
    name: 'HaishinKit.swift',
    url: 'https://github.com/HaishinKit/HaishinKit.swift',
    minimumVersion: '2.2.5',
    products: ['HaishinKit', 'RTMPHaishinKit', 'SRTHaishinKit'],
};

const packageReferenceId = nextId();
const packageProducts = swiftPackage.products.map(product => ({
    product,
    id: nextId(),
    buildFileId: nextId(),
}));

// MARK: - pbxproj 序列化

function quote(value) {
    const text = String(value);
    if (text === '') return '""';
    if (/^[A-Za-z0-9_./]+$/.test(text)) return text;
    return '"' + text.replace(/"/g, '\\"') + '"';
}

const lines = [];
const push = (line = '') => lines.push(line);

const commonSettings = {
    ASSETCATALOG_COMPILER_APPICON_NAME: 'AppIcon',
    ASSETCATALOG_COMPILER_GLOBAL_ACCENT_COLOR_NAME: 'AccentColor',
    CODE_SIGN_STYLE: 'Automatic',
    CURRENT_PROJECT_VERSION: '1',
    ENABLE_PREVIEWS: 'YES',
    GENERATE_INFOPLIST_FILE: 'YES',
    INFOPLIST_KEY_CFBundleDisplayName: 'VideoScopePad',
    INFOPLIST_KEY_LSSupportsOpeningDocumentsInPlace: 'YES',
    INFOPLIST_KEY_NSCameraUsageDescription: '需要访问外接 UVC 采集卡来显示画面、示波器与做 LUT 预览。',
    INFOPLIST_KEY_NSPhotoLibraryAddUsageDescription: '用于把抓帧的画面保存到相册。',
    // iOS 14 起访问局域网设备要用户授权；没有这个键，连权限框都弹不出来，
    // 数据包会被系统静默丢弃 —— SRT / RTMP 连 192.168.x.x 会一直超时。
    INFOPLIST_KEY_NSLocalNetworkUsageDescription: '推流需要连接局域网内的 RTMP / SRT 服务器（例如 192.168.x.x），请允许访问本地网络。',
    // 音频输入（音柱 / 声画延时测量）需要麦克风权限；
    // 采集卡或 USB 声卡的音频同样走这个键（iOS 把它们都当作录音输入）
    INFOPLIST_KEY_NSMicrophoneUsageDescription: '用于显示音频电平（音柱）与测量声画延时（识别测试信号中的千周声）。',
    INFOPLIST_KEY_UIApplicationSupportsIndirectInputEvents: 'YES',
    INFOPLIST_KEY_UIFileSharingEnabled: 'YES',
    INFOPLIST_KEY_UILaunchScreen_Generation: 'YES',
    INFOPLIST_KEY_UIRequiresFullScreen: 'NO',
    INFOPLIST_KEY_UIStatusBarStyle: 'UIStatusBarStyleLightContent',
    INFOPLIST_KEY_UISupportedInterfaceOrientations: 'UIInterfaceOrientationLandscapeLeft UIInterfaceOrientationLandscapeRight UIInterfaceOrientationPortrait UIInterfaceOrientationPortraitUpsideDown',
    INFOPLIST_KEY_UISupportedInterfaceOrientations_iPad: 'UIInterfaceOrientationPortrait UIInterfaceOrientationPortraitUpsideDown UIInterfaceOrientationLandscapeLeft UIInterfaceOrientationLandscapeRight',
    IPHONEOS_DEPLOYMENT_TARGET: '17.0',
    MARKETING_VERSION: appVersion,
    MTL_HEADER_SEARCH_PATHS: '$(SRCROOT)/VideoScopePad/Shaders',
    PRODUCT_BUNDLE_IDENTIFIER: 'com.videoscopepad.app',
    PRODUCT_NAME: '$(TARGET_NAME)',
    SWIFT_EMIT_LOC_STRINGS: 'YES',
    SWIFT_OBJC_BRIDGING_HEADER: 'VideoScopePad/Shaders/VideoScopePad-Bridging-Header.h',
    SWIFT_STRICT_CONCURRENCY: 'minimal',
    SWIFT_VERSION: '5.0',
    TARGETED_DEVICE_FAMILY: '1,2'
};

const projectCommonSettings = {
    ALWAYS_SEARCH_USER_PATHS: 'NO',
    CLANG_ANALYZER_NONNULL: 'YES',
    CLANG_CXX_LANGUAGE_STANDARD: 'gnu++20',
    CLANG_ENABLE_MODULES: 'YES',
    CLANG_ENABLE_OBJC_ARC: 'YES',
    CLANG_WARN_BOOL_CONVERSION: 'YES',
    CLANG_WARN_DOCUMENTATION_COMMENTS: 'YES',
    CLANG_WARN_EMPTY_BODY: 'YES',
    CLANG_WARN_UNREACHABLE_CODE: 'YES',
    COPY_PHASE_STRIP: 'NO',
    ENABLE_STRICT_OBJC_MSGSEND: 'YES',
    ENABLE_USER_SCRIPT_SANDBOXING: 'YES',
    GCC_C_LANGUAGE_STANDARD: 'gnu17',
    GCC_NO_COMMON_BLOCKS: 'YES',
    GCC_WARN_UNINITIALIZED_AUTOS: 'YES',
    IPHONEOS_DEPLOYMENT_TARGET: '17.0',
    MTL_FAST_MATH: 'YES',
    SDKROOT: 'iphoneos',
    SWIFT_EMIT_LOC_STRINGS: 'YES'
};

const projectDebugSettings = {
    ...projectCommonSettings,
    DEBUG_INFORMATION_FORMAT: 'dwarf',
    ENABLE_TESTABILITY: 'YES',
    GCC_DYNAMIC_NO_PIC: 'NO',
    GCC_OPTIMIZATION_LEVEL: '0',
    GCC_PREPROCESSOR_DEFINITIONS: ['DEBUG=1', '$(inherited)'],
    MTL_ENABLE_DEBUG_INFO: 'INCLUDE_SOURCE',
    ONLY_ACTIVE_ARCH: 'YES',
    SWIFT_ACTIVE_COMPILATION_CONDITIONS: 'DEBUG $(inherited)',
    SWIFT_OPTIMIZATION_LEVEL: '-Onone'
};

const projectReleaseSettings = {
    ...projectCommonSettings,
    DEBUG_INFORMATION_FORMAT: 'dwarf-with-dsym',
    ENABLE_NS_ASSERTIONS: 'NO',
    MTL_ENABLE_DEBUG_INFO: 'NO',
    SWIFT_COMPILATION_MODE: 'wholemodule',
    SWIFT_OPTIMIZATION_LEVEL: '-O',
    VALIDATE_PRODUCT: 'YES'
};

/// 值序列化：数组 -> (a, b)；字符串 -> 必要时加引号
function serializeValue(value) {
    if (Array.isArray(value)) {
        return '(' + value.map(v => quote(v)).join(', ') + ')';
    }
    return quote(value);
}

function emitSettings(settings, indent) {
    const pad = ' '.repeat(indent);
    for (const key of Object.keys(settings).sort()) {
        push(`${pad}${quote(key)} = ${serializeValue(settings[key])};`);
    }
}

push('// !$*UTF8*$!');
push('{');
push('\tarchiveVersion = 1;');
push('\tclasses = {');
push('\t};');
push('\tobjectVersion = 56;');
push('\tobjects = {');
push();

// PBXBuildFile
push('/* Begin PBXBuildFile section */');
for (const item of buildFiles) {
    const comment = `${item.name} in ${item.phase}`;
    push(`\t\t${item.id} /* ${comment} */ = {isa = PBXBuildFile; fileRef = ${item.fileRefId} /* ${item.name} */; };`);
}
for (const item of packageProducts) {
    push(`\t\t${item.buildFileId} /* ${item.product} in Frameworks */ = {isa = PBXBuildFile; productRef = ${item.id} /* ${item.product} */; };`);
}
push('/* End PBXBuildFile section */');
push();

// PBXFileReference
push('/* Begin PBXFileReference section */');
push(`\t\t${productRefId} /* ${projectName}.app */ = {isa = PBXFileReference; explicitFileType = wrapper.application; includeInIndex = 0; path = ${projectName}.app; sourceTree = BUILT_PRODUCTS_DIR; };`);
for (const ref of fileRefs) {
    push(`\t\t${ref.id} /* ${ref.name} */ = {isa = PBXFileReference; lastKnownFileType = ${ref.type}; path = ${quote(ref.path)}; sourceTree = "<group>"; };`);
}
push('/* End PBXFileReference section */');
push();

// PBXFrameworksBuildPhase
push('/* Begin PBXFrameworksBuildPhase section */');
push(`\t\t${frameworksBuildPhaseId} /* Frameworks */ = {`);
push('\t\t\tisa = PBXFrameworksBuildPhase;');
push('\t\t\tbuildActionMask = 2147483647;');
push('\t\t\tfiles = (');
for (const item of packageProducts) {
    push(`\t\t\t\t${item.buildFileId} /* ${item.product} in Frameworks */,`);
}
push('\t\t\t);');
push('\t\t\trunOnlyForDeploymentPostprocessing = 0;');
push('\t\t};');
push('/* End PBXFrameworksBuildPhase section */');
push();

// PBXGroup
push('/* Begin PBXGroup section */');

push(`\t\t${mainGroupId} = {`);
push('\t\t\tisa = PBXGroup;');
push('\t\t\tchildren = (');
push(`\t\t\t\t${mainSourceGroupId} /* ${sourcesDirName} */,`);
push(`\t\t\t\t${productsGroupId} /* Products */,`);
push('\t\t\t);');
push('\t\t\tsourceTree = "<group>";');
push('\t\t};');

push(`\t\t${productsGroupId} /* Products */ = {`);
push('\t\t\tisa = PBXGroup;');
push('\t\t\tchildren = (');
push(`\t\t\t\t${productRefId} /* ${projectName}.app */,`);
push('\t\t\t);');
push('\t\t\tname = Products;');
push('\t\t\tsourceTree = "<group>";');
push('\t\t};');

for (const group of groups) {
    push(`\t\t${group.id} /* ${group.name} */ = {`);
    push('\t\t\tisa = PBXGroup;');
    push('\t\t\tchildren = (');
    for (const child of group.children) {
        const ref = fileRefs.find(r => r.id === child);
        const sub = groups.find(g => g.id === child);
        if (ref) push(`\t\t\t\t${ref.id} /* ${ref.name} */,`);
        else if (sub) push(`\t\t\t\t${sub.id} /* ${sub.name} */,`);
    }
    push('\t\t\t);');
    push(`\t\t\tpath = ${quote(group.path)};`);
    push('\t\t\tsourceTree = "<group>";');
    push('\t\t};');
}

push('/* End PBXGroup section */');
push();

// PBXNativeTarget
push('/* Begin PBXNativeTarget section */');
push(`\t\t${targetId} /* ${projectName} */ = {`);
push('\t\t\tisa = PBXNativeTarget;');
push(`\t\t\tbuildConfigurationList = ${targetConfigListId} /* Build configuration list for PBXNativeTarget "${projectName}" */;`);
push('\t\t\tbuildPhases = (');
push(`\t\t\t\t${sourcesBuildPhaseId} /* Sources */,`);
push(`\t\t\t\t${frameworksBuildPhaseId} /* Frameworks */,`);
push(`\t\t\t\t${resourcesBuildPhaseId} /* Resources */,`);
push('\t\t\t);');
push('\t\t\tbuildRules = (');
push('\t\t\t);');
push('\t\t\tdependencies = (');
push('\t\t\t);');
push(`\t\t\tname = ${projectName};`);
push('\t\t\tpackageProductDependencies = (');
for (const item of packageProducts) {
    push(`\t\t\t\t${item.id} /* ${item.product} */,`);
}
push('\t\t\t);');
push(`\t\t\tproductName = ${projectName};`);
push(`\t\t\tproductReference = ${productRefId} /* ${projectName}.app */;`);
push('\t\t\tproductType = "com.apple.product-type.application";');
push('\t\t};');
push('/* End PBXNativeTarget section */');
push();

// PBXProject
push('/* Begin PBXProject section */');
push(`\t\t${projectId} /* Project object */ = {`);
push('\t\t\tisa = PBXProject;');
push('\t\t\tattributes = {');
push('\t\t\t\tBuildIndependentTargetsInParallel = 1;');
push('\t\t\t\tLastSwiftUpdateCheck = 1600;');
push('\t\t\t\tLastUpgradeCheck = 1600;');
push('\t\t\t\tTargetAttributes = {');
push(`\t\t\t\t\t${targetId} = {`);
push('\t\t\t\t\t\tCreatedOnToolsVersion = 16.0;');
push('\t\t\t\t\t};');
push('\t\t\t\t};');
push('\t\t\t};');
push(`\t\t\tbuildConfigurationList = ${projectConfigListId} /* Build configuration list for PBXProject "${projectName}" */;`);
push('\t\t\tcompatibilityVersion = "Xcode 14.0";');
push('\t\t\tdevelopmentRegion = en;');
push('\t\t\thasScannedForEncodings = 0;');
push('\t\t\tknownRegions = (');
push('\t\t\t\ten,');
push('\t\t\t\tBase,');
push('\t\t\t\t"zh-Hans",');
push('\t\t\t);');
push(`\t\t\tmainGroup = ${mainGroupId};`);
push('\t\t\tpackageReferences = (');
push(`\t\t\t\t${packageReferenceId} /* XCRemoteSwiftPackageReference "${swiftPackage.name}" */,`);
push('\t\t\t);');
push(`\t\t\tproductRefGroup = ${productsGroupId} /* Products */;`);
push('\t\t\tprojectDirPath = "";');
push('\t\t\tprojectRoot = "";');
push('\t\t\ttargets = (');
push(`\t\t\t\t${targetId} /* ${projectName} */,`);
push('\t\t\t);');
push('\t\t};');
push('/* End PBXProject section */');
push();

// PBXResourcesBuildPhase
push('/* Begin PBXResourcesBuildPhase section */');
push(`\t\t${resourcesBuildPhaseId} /* Resources */ = {`);
push('\t\t\tisa = PBXResourcesBuildPhase;');
push('\t\t\tbuildActionMask = 2147483647;');
push('\t\t\tfiles = (');
for (const item of buildFiles.filter(b => b.phase === 'Resources')) {
    push(`\t\t\t\t${item.id} /* ${item.name} in Resources */,`);
}
push('\t\t\t);');
push('\t\t\trunOnlyForDeploymentPostprocessing = 0;');
push('\t\t};');
push('/* End PBXResourcesBuildPhase section */');
push();

// PBXSourcesBuildPhase
push('/* Begin PBXSourcesBuildPhase section */');
push(`\t\t${sourcesBuildPhaseId} /* Sources */ = {`);
push('\t\t\tisa = PBXSourcesBuildPhase;');
push('\t\t\tbuildActionMask = 2147483647;');
push('\t\t\tfiles = (');
for (const item of buildFiles.filter(b => b.phase === 'Sources')) {
    push(`\t\t\t\t${item.id} /* ${item.name} in Sources */,`);
}
push('\t\t\t);');
push('\t\t\trunOnlyForDeploymentPostprocessing = 0;');
push('\t\t};');
push('/* End PBXSourcesBuildPhase section */');
push();

// XCRemoteSwiftPackageReference
push('/* Begin XCRemoteSwiftPackageReference section */');
push(`\t\t${packageReferenceId} /* XCRemoteSwiftPackageReference "${swiftPackage.name}" */ = {`);
push('\t\t\tisa = XCRemoteSwiftPackageReference;');
push(`\t\t\trepositoryURL = "${swiftPackage.url}";`);
push('\t\t\trequirement = {');
push('\t\t\t\tkind = upToNextMajorVersion;');
push(`\t\t\t\tminimumVersion = ${swiftPackage.minimumVersion};`);
push('\t\t\t};');
push('\t\t};');
push('/* End XCRemoteSwiftPackageReference section */');
push();

// XCSwiftPackageProductDependency
push('/* Begin XCSwiftPackageProductDependency section */');
for (const item of packageProducts) {
    push(`\t\t${item.id} /* ${item.product} */ = {`);
    push('\t\t\tisa = XCSwiftPackageProductDependency;');
    push(`\t\t\tpackage = ${packageReferenceId} /* XCRemoteSwiftPackageReference "${swiftPackage.name}" */;`);
    push(`\t\t\tproductName = ${item.product};`);
    push('\t\t};');
}
push('/* End XCSwiftPackageProductDependency section */');
push();

// XCBuildConfiguration
push('/* Begin XCBuildConfiguration section */');

push(`\t\t${projectDebugConfigId} /* Debug */ = {`);
push('\t\t\tisa = XCBuildConfiguration;');
push('\t\t\tbuildSettings = {');
emitSettings(projectDebugSettings, 4);
push('\t\t\t};');
push('\t\t\tname = Debug;');
push('\t\t};');

push(`\t\t${projectReleaseConfigId} /* Release */ = {`);
push('\t\t\tisa = XCBuildConfiguration;');
push('\t\t\tbuildSettings = {');
emitSettings(projectReleaseSettings, 4);
push('\t\t\t};');
push('\t\t\tname = Release;');
push('\t\t};');

push(`\t\t${targetDebugConfigId} /* Debug */ = {`);
push('\t\t\tisa = XCBuildConfiguration;');
push('\t\t\tbuildSettings = {');
emitSettings(commonSettings, 4);
push('\t\t\t};');
push('\t\t\tname = Debug;');
push('\t\t};');

push(`\t\t${targetReleaseConfigId} /* Release */ = {`);
push('\t\t\tisa = XCBuildConfiguration;');
push('\t\t\tbuildSettings = {');
emitSettings(commonSettings, 4);
push('\t\t\t};');
push('\t\t\tname = Release;');
push('\t\t};');

push('/* End XCBuildConfiguration section */');
push();

// XCConfigurationList
push('/* Begin XCConfigurationList section */');

push(`\t\t${projectConfigListId} /* Build configuration list for PBXProject "${projectName}" */ = {`);
push('\t\t\tisa = XCConfigurationList;');
push('\t\t\tbuildConfigurations = (');
push(`\t\t\t\t${projectDebugConfigId} /* Debug */,`);
push(`\t\t\t\t${projectReleaseConfigId} /* Release */,`);
push('\t\t\t);');
push('\t\t\tdefaultConfigurationIsVisible = 0;');
push('\t\t\tdefaultConfigurationName = Release;');
push('\t\t};');

push(`\t\t${targetConfigListId} /* Build configuration list for PBXNativeTarget "${projectName}" */ = {`);
push('\t\t\tisa = XCConfigurationList;');
push('\t\t\tbuildConfigurations = (');
push(`\t\t\t\t${targetDebugConfigId} /* Debug */,`);
push(`\t\t\t\t${targetReleaseConfigId} /* Release */,`);
push('\t\t\t);');
push('\t\t\tdefaultConfigurationIsVisible = 0;');
push('\t\t\tdefaultConfigurationName = Release;');
push('\t\t};');

push('/* End XCConfigurationList section */');
push();

push('\t};');
push(`\trootObject = ${projectId} /* Project object */;`);
push('}');

const pbxproj = lines.join('\n') + '\n';

// MARK: - 写入磁盘

const xcodeprojDir = path.join(rootDir, `${projectName}.xcodeproj`);
const schemeDir = path.join(xcodeprojDir, 'xcshareddata', 'xcschemes');
fs.mkdirSync(schemeDir, { recursive: true });
fs.writeFileSync(path.join(xcodeprojDir, 'project.pbxproj'), pbxproj, 'utf8');

const scheme = `<?xml version="1.0" encoding="UTF-8"?>
<Scheme
   LastUpgradeVersion = "1600"
   version = "1.7">
   <BuildAction
      parallelizeBuildables = "YES"
      buildImplicitDependencies = "YES">
      <BuildActionEntries>
         <BuildActionEntry
            buildForTesting = "YES"
            buildForRunning = "YES"
            buildForProfiling = "YES"
            buildForArchiving = "YES"
            buildForAnalyzing = "YES">
            <BuildableReference
               BuildableIdentifier = "primary"
               BlueprintIdentifier = "${targetId}"
               BuildableName = "${projectName}.app"
               BlueprintName = "${projectName}"
               ReferencedContainer = "container:${projectName}.xcodeproj">
            </BuildableReference>
         </BuildActionEntry>
      </BuildActionEntries>
   </BuildAction>
   <TestAction
      buildConfiguration = "Debug"
      selectedDebuggerIdentifier = "Xcode.DebuggerFoundation.Debugger.LLDB"
      selectedLauncherIdentifier = "Xcode.DebuggerFoundation.Launcher.LLDB"
      shouldUseLaunchSchemeArgsEnv = "YES">
      <Testables>
      </Testables>
   </TestAction>
   <LaunchAction
      buildConfiguration = "Debug"
      selectedDebuggerIdentifier = "Xcode.DebuggerFoundation.Debugger.LLDB"
      selectedLauncherIdentifier = "Xcode.DebuggerFoundation.Launcher.LLDB"
      launchStyle = "0"
      useCustomWorkingDirectory = "NO"
      ignoresPersistentStateOnLaunch = "NO"
      debugDocumentVersioning = "YES"
      debugServiceExtension = "internal"
      allowLocationSimulation = "YES">
      <BuildableProductRunnable
         runnableDebuggingMode = "0">
         <BuildableReference
            BuildableIdentifier = "primary"
            BlueprintIdentifier = "${targetId}"
            BuildableName = "${projectName}.app"
            BlueprintName = "${projectName}"
            ReferencedContainer = "container:${projectName}.xcodeproj">
         </BuildableReference>
      </BuildableProductRunnable>
   </LaunchAction>
   <ProfileAction
      buildConfiguration = "Release"
      shouldUseLaunchSchemeArgsEnv = "YES"
      savedToolIdentifier = ""
      useCustomWorkingDirectory = "NO"
      debugDocumentVersioning = "YES">
      <BuildableProductRunnable
         runnableDebuggingMode = "0">
         <BuildableReference
            BuildableIdentifier = "primary"
            BlueprintIdentifier = "${targetId}"
            BuildableName = "${projectName}.app"
            BlueprintName = "${projectName}"
            ReferencedContainer = "container:${projectName}.xcodeproj">
         </BuildableReference>
      </BuildableProductRunnable>
   </ProfileAction>
   <AnalyzeAction
      buildConfiguration = "Debug">
   </AnalyzeAction>
   <ArchiveAction
      buildConfiguration = "Release"
      revealArchiveInOrganizer = "YES">
   </ArchiveAction>
</Scheme>
`;
fs.writeFileSync(path.join(schemeDir, `${projectName}.xcscheme`), scheme, 'utf8');

// MARK: - 结果

const fileCount = fileRefs.length;
const sourceCount = buildFiles.filter(b => b.phase === 'Sources').length;
console.log(`已生成 ${projectName}.xcodeproj`);
console.log(`  源文件（编译）: ${sourceCount}`);
console.log(`  资源: ${buildFiles.filter(b => b.phase === 'Resources').length}`);
console.log(`  其它文件引用: ${fileCount - buildFiles.length}`);
console.log(`  scheme: ${path.relative(rootDir, path.join(schemeDir, `${projectName}.xcscheme`))}`);
