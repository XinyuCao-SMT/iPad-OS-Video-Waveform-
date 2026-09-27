//
//  GeometryHelpers.swift
//  VideoScopePad
//
//  单位空间 <-> 视图坐标的换算辅助。
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

extension ScopeLayoutResult: Equatable {
    static func == (lhs: ScopeLayoutResult, rhs: ScopeLayoutResult) -> Bool {
        lhs.monitorRect == rhs.monitorRect
            && lhs.monitorUV == rhs.monitorUV
            && lhs.plots == rhs.plots
            && lhs.isOverlay == rhs.isOverlay
    }
}
