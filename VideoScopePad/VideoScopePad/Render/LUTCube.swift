//
//  LUTCube.swift
//  VideoScopePad
//
//  Adobe .cube（IRIDAS/Resolve 通用）3D LUT 与 1D shaper LUT 解析器。
//

import Foundation
import simd

struct CubeLUT {
    var title: String = ""
    var size1D: Int = 0
    var size3D: Int = 0
    var domainMin = SIMD3<Float>(0, 0, 0)
    var domainMax = SIMD3<Float>(1, 1, 1)
    /// RGBA16F 数据（每像素 4 个半精度浮点，A 恒为 1）
    var data1D: [Float16] = []
    var data3D: [Float16] = []
    var warnings: [String] = []
}

enum CubeLUTError: LocalizedError {
    case unreadable(String)
    case empty
    case missingSize
    case insufficientData(String)
    case tooLarge(Int)

    var errorDescription: String? {
        switch self {
        case .unreadable(let text): return "无法读取文件：\(text)"
        case .empty: return "文件里没有任何 LUT 内容"
        case .missingSize: return "文件里没有 LUT_1D_SIZE 或 LUT_3D_SIZE 声明"
        case .insufficientData(let text): return "LUT 数据不完整：\(text)"
        case .tooLarge(let size): return "LUT 尺寸 \(size) 过大（建议 ≤ 65）"
        }
    }
}

enum CubeLUTParser {

    static func parse(contentsOf url: URL) throws -> CubeLUT {
        let data: Data
        do {
            data = try Data(contentsOf: url)
        } catch {
            throw CubeLUTError.unreadable(error.localizedDescription)
        }

        let text = String(data: data, encoding: .utf8)
            ?? String(data: data, encoding: .isoLatin1)
        guard let text else {
            throw CubeLUTError.unreadable("编码无法识别")
        }
        return try parse(text: text)
    }

    static func parse(text: String) throws -> CubeLUT {
        var lut = CubeLUT()
        var raw1D: [Float] = []
        var raw3D: [Float] = []

        let lines = text.split(separator: "\n", omittingEmptySubsequences: false)

        for rawLine in lines {
            let line = rawLine.trimmingCharacters(in: .whitespacesAndNewlines)
            if line.isEmpty || line.hasPrefix("#") { continue }

            let upper = line.uppercased()

            if upper.hasPrefix("TITLE") {
                lut.title = firstQuoted(in: line) ?? ""
                continue
            }
            if upper.hasPrefix("LUT_1D_SIZE") {
                lut.size1D = lastInteger(in: line) ?? 0
                continue
            }
            if upper.hasPrefix("LUT_3D_SIZE") {
                lut.size3D = lastInteger(in: line) ?? 0
                continue
            }
            if upper.hasPrefix("LUT_1D_INPUT_RANGE") || upper.hasPrefix("LUT_3D_INPUT_RANGE") {
                let values = floats(in: line)
                if values.count >= 2 {
                    lut.domainMin = SIMD3<Float>(repeating: values[0])
                    lut.domainMax = SIMD3<Float>(repeating: values[1])
                }
                continue
            }
            if upper.hasPrefix("DOMAIN_MIN") {
                let values = floats(in: line)
                if values.count >= 3 {
                    lut.domainMin = SIMD3<Float>(values[0], values[1], values[2])
                }
                continue
            }
            if upper.hasPrefix("DOMAIN_MAX") {
                let values = floats(in: line)
                if values.count >= 3 {
                    lut.domainMax = SIMD3<Float>(values[0], values[1], values[2])
                }
                continue
            }
            // 其余视为数据行
            if upper.first?.isLetter == true { continue }

            let values = floats(in: line)
            if values.count >= 3 {
                if lut.size1D > 0 && raw1D.count < lut.size1D * 3 {
                    raw1D.append(values[0])
                    raw1D.append(values[1])
                    raw1D.append(values[2])
                } else {
                    raw3D.append(values[0])
                    raw3D.append(values[1])
                    raw3D.append(values[2])
                }
            }
        }

        guard lut.size1D > 0 || lut.size3D > 0 else {
            throw CubeLUTError.missingSize
        }
        if lut.size3D > 129 {
            throw CubeLUTError.tooLarge(lut.size3D)
        }

        if lut.size1D > 0 {
            let expected = lut.size1D * 3
            guard raw1D.count >= expected else {
                throw CubeLUTError.insufficientData("1D LUT 需要 \(expected) 个数值，实际 \(raw1D.count)")
            }
            var data = [Float16](repeating: 0, count: lut.size1D * 4)
            for index in 0..<lut.size1D {
                data[index * 4 + 0] = half(raw1D[index * 3 + 0])
                data[index * 4 + 1] = half(raw1D[index * 3 + 1])
                data[index * 4 + 2] = half(raw1D[index * 3 + 2])
                data[index * 4 + 3] = 1
            }
            lut.data1D = data
        }

        if lut.size3D > 1 {
            let count = lut.size3D * lut.size3D * lut.size3D
            let expected = count * 3
            guard raw3D.count >= expected else {
                throw CubeLUTError.insufficientData("3D LUT 需要 \(expected) 个数值（\(lut.size3D)³），实际 \(raw3D.count)")
            }
            var data = [Float16](repeating: 0, count: count * 4)
            for index in 0..<count {
                data[index * 4 + 0] = half(raw3D[index * 3 + 0])
                data[index * 4 + 1] = half(raw3D[index * 3 + 1])
                data[index * 4 + 2] = half(raw3D[index * 3 + 2])
                data[index * 4 + 3] = 1
            }
            lut.data3D = data
        }

        return lut
    }

    // MARK: - 解析辅助

    private static func half(_ value: Float) -> Float16 {
        // 允许 LUT 输出略微超出 0-1（HDR/超白），但防止溢出成 inf
        let clamped = min(max(value, -0.5), 8.0)
        return Float16(clamped)
    }

    private static func firstQuoted(in line: String) -> String? {
        guard let start = line.firstIndex(of: "\"") else { return nil }
        let rest = line[line.index(after: start)...]
        guard let end = rest.firstIndex(of: "\"") else { return nil }
        return String(rest[rest.startIndex..<end])
    }

    private static func lastInteger(in line: String) -> Int? {
        let parts = line.split(whereSeparator: { $0 == " " || $0 == "\t" })
        guard let last = parts.last else { return nil }
        return Int(last)
    }

    private static func floats(in line: String) -> [Float] {
        var result: [Float] = []
        result.reserveCapacity(3)
        for part in line.split(whereSeparator: { $0 == " " || $0 == "\t" || $0 == "," }) {
            if let value = Float(part) {
                result.append(value)
                if result.count == 3 { break }
            }
        }
        return result
    }
}
