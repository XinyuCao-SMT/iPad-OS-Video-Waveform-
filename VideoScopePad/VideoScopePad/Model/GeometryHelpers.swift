//
//  GeometryHelpers.swift
//  VideoScopePad
//
//  单位空间 <-> 视图坐标的换算辅助。
//
//  注：ScopeLayoutResult / PaneLayout 的 Equatable 在 ScopeLayout.swift 里由编译器合成
//  （同文件声明，避免跨文件的合成限制），所以这里不再手动实现 ==。
//

import CoreGraphics

extension CGRect {
    /// 单位空间矩形（0...1）映射到给定尺寸
    func scaled(to size: CGSize) -> CGRect {
        CGRect(x: minX * size.width,
               y: minY * size.height,
               width: width * size.width,
               height: height * size.height)
    }
}
