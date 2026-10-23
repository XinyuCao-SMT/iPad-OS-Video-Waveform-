//
//  DiagnosticsLogView.swift
//  VideoScopePad
//
//  App 内直接看诊断日志（不依赖「文件」App 的文件共享）。
//
//  为什么要有这个页：现场卡死时用户根本拿不到 hanglog.txt（实测「文件」App 里看不到），
//  所以把日志搬进界面：能滚动、能一键复制，复制后直接贴给我就行。
//

import SwiftUI
import UIKit

struct DiagnosticsLogView: View {

    @State private var text: String = ""
    @State private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(HangLog.logPath)
                .font(.system(size: 10, design: .monospaced))
                .foregroundStyle(.secondary)
                .textSelection(.enabled)
                .padding(.horizontal, 12)

            ScrollView {
                Text(text.isEmpty ? "（日志还是空的：说明 HangLog 没被调用，或者还没发生任何操作）" : text)
                    .font(.system(size: 10, design: .monospaced))
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(12)
            }

            HStack {
                Button {
                    UIPasteboard.general.string = text
                    copied = true
                    DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { copied = false }
                } label: {
                    Label(copied ? "已复制" : "复制全部", systemImage: "doc.on.doc")
                }
                .buttonStyle(.borderedProminent)

                Button {
                    HangLog.mark("用户打开诊断日志")
                    refresh()
                } label: {
                    Label("刷新", systemImage: "arrow.clockwise")
                }
                .buttonStyle(.bordered)
            }
            .padding(.horizontal, 12)
        }
        .navigationTitle("诊断日志")
        .navigationBarTitleDisplayMode(.inline)
        .onAppear {
            HangLog.mark("打开诊断日志页")
            refresh()
        }
    }

    private func refresh() {
        text = HangLog.tail(lines: 400)
    }
}
