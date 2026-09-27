//
//  Controls.swift
//  VideoScopePad
//
//  控制栏里复用的小控件。
//

import SwiftUI

struct ChipLabel: View {
    let title: String
    var systemImage: String?
    var isActive: Bool = true
    var tint: Color?

    var body: some View {
        HStack(spacing: 4) {
            if let systemImage {
                Image(systemName: systemImage)
                    .font(.system(size: 11, weight: .semibold))
            }
            Text(title)
                .font(.system(size: 12, weight: .medium))
                .lineLimit(1)
        }
        .padding(.horizontal, 9)
        .padding(.vertical, 6)
        .background(background)
        .foregroundStyle(foreground)
        .clipShape(RoundedRectangle(cornerRadius: 7, style: .continuous))
    }

    private var background: Color {
        if let tint, isActive { return tint.opacity(0.30) }
        return isActive ? Color.white.opacity(0.14) : Color.white.opacity(0.06)
    }

    private var foreground: Color {
        isActive ? Color.white : Color.white.opacity(0.5)
    }
}

struct LabeledSlider: View {
    let title: String
    @Binding var value: Double
    let range: ClosedRange<Double>
    let format: String
    var onReset: (() -> Void)?

    var body: some View {
        VStack(alignment: .leading, spacing: 1) {
            HStack(spacing: 4) {
                Text(title)
                    .font(.system(size: 11))
                    .foregroundStyle(.white.opacity(0.7))
                Spacer(minLength: 4)
                Text(String(format: format, value))
                    .font(.system(size: 11, design: .monospaced))
                    .foregroundStyle(.white.opacity(0.9))
            }
            Slider(value: $value, in: range)
                .controlSize(.mini)
                .tint(.white.opacity(0.8))
        }
    }
}

struct MenuSectionTitle: View {
    let text: String
    var body: some View {
        Text(text)
            .font(.system(size: 11, weight: .semibold))
            .foregroundStyle(.white.opacity(0.45))
    }
}
