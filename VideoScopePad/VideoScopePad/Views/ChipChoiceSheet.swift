//
//  ChipChoiceSheet.swift
//  VideoScopePad
//
//  统一样式的「选项列表」弹窗（设备 / 格式 / 布局 / 显示通道 / 波形模式 / LUT /
//  格子内容 / 刻度单位 共用）。
//
//  为什么自绘：SwiftUI 的 Menu 在本 App 里会「点不开 / 第二次不弹」（界面像卡死），
//  所以全部改用这套：NavigationStack + List，当前项右侧打勾，左上「关闭」。
//

import SwiftUI

struct ChipChoiceSheet: View {

    struct Row: Identifiable {
        let id = UUID()
        let title: String
        let isSelected: Bool
        let action: () -> Void
    }

    /// 分组（⚠️ 不能叫 Section：会遮住 SwiftUI.Section，body 里的 Section 会被解析成自己）
    struct ChoiceSection: Identifiable {
        let id = UUID()
        let title: String
        let rows: [Row]
    }

    let title: String
    let sections: [ChoiceSection]
    /// 底部说明（可选）
    var footer: String? = nil

    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            List {
                ForEach(sections) { section in
                    Section {
                        ForEach(section.rows) { row in
                            Button {
                                row.action()
                                dismiss()
                            } label: {
                                HStack {
                                    Text(row.title)
                                    Spacer()
                                    if row.isSelected {
                                        Image(systemName: "checkmark")
                                            .foregroundStyle(.tint)
                                    }
                                }
                            }
                            .buttonStyle(.plain)
                        }
                    } header: {
                        if !section.title.isEmpty {
                            Text(section.title)
                        }
                    } footer: {
                        if let footer, section.id == sections.last?.id {
                            Text(footer)
                        }
                    }
                }
            }
            .navigationTitle(title)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("关闭") { dismiss() }
                }
            }
        }
    }

    /// 单组列表的便捷构造
    static func single(title: String, rows: [Row], footer: String? = nil) -> ChipChoiceSheet {
        ChipChoiceSheet(title: title,
                        sections: [ChoiceSection(title: "", rows: rows)],
                        footer: footer)
    }
}
