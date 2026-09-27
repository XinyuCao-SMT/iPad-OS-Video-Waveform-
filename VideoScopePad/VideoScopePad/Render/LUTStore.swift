//
//  LUTStore.swift
//  VideoScopePad
//
//  LUT 文件管理：导入 .cube、列出、加载成 Metal 纹理。
//  文件存放在沙盒的 Documents/LUTs 下（在「文件」App 里可以看到并直接放文件进去）。
//

import Combine
import Foundation
import Metal

final class LUTStore: ObservableObject {

    struct Item: Identifiable, Hashable {
        let id: String
        let name: String
        let url: URL
        let byteCount: Int64

        var sizeText: String {
            ByteCountFormatter.string(fromByteCount: byteCount, countStyle: .file)
        }
    }

    @Published private(set) var items: [Item] = []
    @Published private(set) var selectedID: String?
    @Published private(set) var isLoading = false
    @Published private(set) var statusText: String?
    @Published private(set) var detailText: String?
    @Published private(set) var errorText: String?

    let slot = LUTSlot()

    private var device: MTLDevice?
    private var placeholder: LUTTextures?
    private let queue = DispatchQueue(label: "com.videoscopepad.lut", qos: .userInitiated)
    private let selectedKey = "vsp.selectedLUT"

    var directoryURL: URL {
        let documents = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        return documents.appendingPathComponent("LUTs", isDirectory: true)
    }

    // MARK: - 配置

    func configure(device: MTLDevice?, placeholder: LUTTextures?) {
        self.device = device
        do {
            try FileManager.default.createDirectory(at: directoryURL, withIntermediateDirectories: true)
        } catch {
            errorText = "无法创建 LUT 目录：\(error.localizedDescription)"
        }

        if let placeholder {
            self.placeholder = placeholder
            slot.set(placeholder)
        } else if let device, let made = try? LUTTextureBuilder.makePlaceholder(device: device) {
            self.placeholder = made
            slot.set(made)
        }

        reload()
        select(id: UserDefaults.standard.string(forKey: selectedKey))
    }

    // MARK: - 列表

    func reload() {
        do {
            let urls = try FileManager.default.contentsOfDirectory(at: directoryURL,
                                                                   includingPropertiesForKeys: [.fileSizeKey],
                                                                   options: [.skipsHiddenFiles])
            let list = urls
                .filter { $0.pathExtension.lowercased() == "cube" }
                .map { url -> Item in
                    let size = (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
                    return Item(id: url.lastPathComponent,
                                name: url.deletingPathExtension().lastPathComponent,
                                url: url,
                                byteCount: Int64(size))
                }
                .sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
            items = list
            if let selectedID, !list.contains(where: { $0.id == selectedID }) {
                self.selectedID = nil
                slot.set(placeholder)
                detailText = nil
            }
        } catch {
            errorText = "读取 LUT 目录失败：\(error.localizedDescription)"
        }
    }

    // MARK: - 导入 / 选择 / 删除

    func importFile(from url: URL) {
        let accessed = url.startAccessingSecurityScopedResource()
        defer { if accessed { url.stopAccessingSecurityScopedResource() } }

        do {
            try FileManager.default.createDirectory(at: directoryURL, withIntermediateDirectories: true)

            var destination = directoryURL.appendingPathComponent(url.lastPathComponent)
            if FileManager.default.fileExists(atPath: destination.path) {
                let base = url.deletingPathExtension().lastPathComponent
                let ext = url.pathExtension
                destination = directoryURL.appendingPathComponent("\(base)-\(Int(Date().timeIntervalSince1970)).\(ext)")
            }
            try FileManager.default.copyItem(at: url, to: destination)
            statusText = "已导入 \(destination.lastPathComponent)"
            errorText = nil
            reload()
            select(id: destination.lastPathComponent)
        } catch {
            errorText = "导入失败：\(error.localizedDescription)"
        }
    }

    func select(id: String?) {
        selectedID = id
        UserDefaults.standard.set(id, forKey: selectedKey)

        guard let id, let item = items.first(where: { $0.id == id }) else {
            slot.set(placeholder)
            detailText = nil
            return
        }

        guard let device else {
            errorText = "Metal 设备不可用，无法加载 LUT"
            return
        }

        isLoading = true
        errorText = nil

        queue.async { [weak self] in
            guard let self else { return }
            do {
                let cube = try CubeLUTParser.parse(contentsOf: item.url)
                let textures = try LUTTextureBuilder.make(from: cube, device: device)
                DispatchQueue.main.async {
                    self.slot.set(textures)
                    self.detailText = "\(textures.title) · \(textures.description)"
                    self.isLoading = false
                    self.errorText = nil
                }
            } catch {
                let text = error.localizedDescription
                DispatchQueue.main.async {
                    self.errorText = text
                    self.isLoading = false
                    self.slot.set(self.placeholder)
                    self.detailText = nil
                }
            }
        }
    }

    func delete(id: String) {
        guard let item = items.first(where: { $0.id == id }) else { return }
        do {
            try FileManager.default.removeItem(at: item.url)
            if selectedID == id {
                select(id: nil)
            }
            reload()
        } catch {
            errorText = "删除失败：\(error.localizedDescription)"
        }
    }

    func clearMessages() {
        statusText = nil
        errorText = nil
    }
}
