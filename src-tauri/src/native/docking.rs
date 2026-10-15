/// 用户设置的外框宽度以 DIP 保存，定位时按侧栏自身 DPI 转换，与界面内容的缩放一致。
/// 只接纳真实拖拽调整大小，不把跟随裁剪、边框补偿或 DPI 通知产生的宽度记成用户选择。
#[derive(Default)]
pub struct DockWidth {
    logical: Option<f64>,
}
impl DockWidth {
    pub fn observe(&mut self, width: i32, dpi: u32, resizing: bool) {
        if self.logical.is_none() || (resizing && width != self.physical(dpi)) {
            self.logical = Some((f64::from(width) * 96.0 / f64::from(dpi.max(96))).max(300.0));
        }
    }
    pub fn physical(&self, dpi: u32) -> i32 {
        (self.logical.unwrap_or(300.0) * f64::from(dpi.max(96)) / 96.0).round() as i32
    }
}

/// SetWindowPos 使用窗口外框，而 DWM 给出的可见边缘不包含不可见的调整大小边框。
/// 将期望的可见矩形换算成外框矩形；边框来自当前窗口，避免写死像素后在不同 DPI 下出现间隙。
pub fn window_bounds_for_frame(
    desired: &windows::Win32::Foundation::RECT,
    outer: &windows::Win32::Foundation::RECT,
    frame: &windows::Win32::Foundation::RECT,
) -> windows::Win32::Foundation::RECT {
    windows::Win32::Foundation::RECT {
        left: desired.left - (frame.left - outer.left),
        top: desired.top - (frame.top - outer.top),
        right: desired.right + (outer.right - frame.right),
        bottom: desired.bottom + (outer.bottom - frame.bottom),
    }
}

/// 侧栏始终贴在目标的指定外侧，上下对齐且保留用户宽度。
/// 不按工作区回挪或隐藏；超出屏幕或被任务栏遮挡时仍保持几何，避免覆盖目标内容。
pub fn sidebar_bounds(
    target: &windows::Win32::Foundation::RECT,
    width: i32,
    left: bool,
) -> windows::Win32::Foundation::RECT {
    let width = width.max(1);
    let x = if left {
        target.left - width
    } else {
        target.right
    };
    windows::Win32::Foundation::RECT {
        left: x,
        top: target.top,
        right: x + width,
        bottom: target.bottom,
    }
}

/// 独立窗口默认靠当前工作区右侧，超大尺寸先收进工作区；负坐标屏幕同样按其边界计算。
pub fn standalone_bounds(
    work: &windows::Win32::Foundation::RECT,
    width: i32,
    height: i32,
    dpi: u32,
) -> windows::Win32::Foundation::RECT {
    let width = width.clamp(1, (work.right - work.left).max(1));
    let height = height.clamp(1, (work.bottom - work.top).max(1));
    let margin = (12.0 * f64::from(dpi.max(96)) / 96.0).round() as i32;
    let left = (work.right - width - margin).max(work.left);
    let top = work.top + ((work.bottom - work.top - height) / 2).max(0);
    windows::Win32::Foundation::RECT {
        left,
        top,
        right: left + width,
        bottom: top + height,
    }
}

/// 两段高度使用同一 DPI 换算；基础短语区从目标底边开始，输入行不随向上搜索覆盖层跳动。
pub fn bottom_bounds(
    target: &windows::Win32::Foundation::RECT,
    height: i32,
    above: i32,
    dpi: u32,
) -> windows::Win32::Foundation::RECT {
    let scale = f64::from(dpi.max(96)) / 96.0;
    let top = target.bottom - (f64::from(above) * scale).round() as i32;
    windows::Win32::Foundation::RECT {
        left: target.left,
        top,
        right: target.right,
        bottom: top + (f64::from(height) * scale).round() as i32,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn standalone_stays_inside_current_work_area_at_each_dpi() {
        use windows::Win32::Foundation::RECT;
        for dpi in [96, 144, 192] {
            let work = RECT {
                left: -2560,
                top: -240,
                right: 0,
                bottom: 1200,
            };
            let width = 330 * dpi as i32 / 96;
            let height = 720 * dpi as i32 / 96;
            let bounds = standalone_bounds(&work, width, height, dpi);
            assert_eq!(work.right - bounds.right, 12 * dpi as i32 / 96);
            assert_eq!(bounds.top - work.top, (work.bottom - bounds.bottom).max(0));
            assert!(bounds.left >= work.left && bounds.bottom <= work.bottom);
            let oversized = standalone_bounds(&work, 4000, 3000, dpi);
            assert_eq!(
                (
                    oversized.left,
                    oversized.top,
                    oversized.right,
                    oversized.bottom
                ),
                (work.left, work.top, work.right, work.bottom)
            );
        }
    }
    #[test]
    fn search_expansion_keeps_base_area_at_target_bottom_at_each_dpi() {
        use windows::Win32::Foundation::RECT;
        let target = RECT {
            left: -1200,
            top: 30,
            right: -200,
            bottom: 800,
        };
        for dpi in [96, 144, 192] {
            for above in [0, 29, 84, 253, 362] {
                let rect = bottom_bounds(&target, above + 58, above, dpi);
                assert_eq!(
                    rect.top + (f64::from(above) * f64::from(dpi) / 96.0).round() as i32,
                    target.bottom
                );
                assert_eq!((rect.left, rect.right), (target.left, target.right));
                assert_eq!(rect.bottom, target.bottom + 58 * dpi as i32 / 96);
            }
        }
    }
    #[test]
    fn overlapping_search_preserves_input_position_with_multiline_phrases() {
        use windows::Win32::Foundation::RECT;
        let target = RECT {
            left: -1200,
            top: 30,
            right: -200,
            bottom: 800,
        };
        for dpi in [96, 120, 144, 192] {
            let scale = f64::from(dpi) / 96.0;
            for common in [0, 28, 55, 82, 150] {
                let expected = target.bottom + (f64::from(common) * scale).round() as i32;
                for overlay in [0, 28, 90, 281] {
                    let above = i32::max(overlay - common, 0);
                    let rect = bottom_bounds(&target, common + 30 + above, above, dpi);
                    let input = rect.top + (f64::from(above + common) * scale).round() as i32;
                    assert!((input - expected).abs() <= 1);
                }
            }
        }
    }
    #[test]
    fn sidebar_at_screen_edge_stays_on_configured_side_with_full_width() {
        use windows::Win32::Foundation::RECT;
        for (target, preferred_left, expected_left) in [
            (
                RECT {
                    left: 900,
                    top: 100,
                    right: 1850,
                    bottom: 750,
                },
                false,
                1850,
            ),
            (
                RECT {
                    left: 30,
                    top: 100,
                    right: 980,
                    bottom: 750,
                },
                true,
                -300,
            ),
        ] {
            let bounds = sidebar_bounds(&target, 330, preferred_left);
            assert_eq!(bounds.left, expected_left);
            assert_eq!(bounds.right - bounds.left, 330);
            assert!(bounds.right <= target.left || bounds.left >= target.right);
        }
    }
    #[test]
    fn sidebar_follows_offscreen_target_without_overlap_at_each_dpi() {
        use windows::Win32::Foundation::RECT;
        for dpi in [96, 144, 192] {
            let width = 330 * dpi / 96;
            for (x1, y1, x2, y2) in [
                (-180, 100, -20, 750),    // 负坐标且目标比侧栏窄。
                (-500, -120, 2200, 1400), // 目标跨越四个屏幕边缘。
                (100, 1200, 880, 1600),   // 目标完全移到屏幕下方。
                (100, -800, 880, -200),   // 目标完全移到屏幕上方。
            ] {
                let target = RECT {
                    left: x1 * dpi / 96,
                    top: y1 * dpi / 96,
                    right: x2 * dpi / 96,
                    bottom: y2 * dpi / 96,
                };
                for left in [false, true] {
                    let bounds = sidebar_bounds(&target, width, left);
                    assert_eq!(bounds.right - bounds.left, width);
                    assert_eq!((bounds.top, bounds.bottom), (target.top, target.bottom));
                    assert_eq!(
                        if left { bounds.right } else { bounds.left },
                        if left { target.left } else { target.right }
                    );
                    assert!(bounds.right <= target.left || bounds.left >= target.right);
                }
            }
        }
    }
    #[test]
    fn logical_width_survives_following_clipping_and_dpi_round_trips() {
        let mut width = DockWidth::default();
        width.observe(330, 96, false);
        for (dpi, expected) in [(96, 330), (144, 495), (192, 660), (96, 330)] {
            assert_eq!(width.physical(dpi), expected);
            width.observe(18, dpi, false); // 旧细条或工作区裁剪不能覆盖用户宽度。
            assert_eq!(width.physical(dpi), expected);
        }
    }
    #[test]
    fn real_resize_updates_logical_width_without_remembering_dpi_scaling() {
        let mut width = DockWidth::default();
        width.observe(495, 144, false);
        width.observe(660, 192, true); // 拖动跨屏时的 DPI 换算不是改宽。
        assert_eq!(width.physical(96), 330);
        width.observe(760, 192, true);
        assert_eq!(width.physical(96), 380);
        assert_eq!(width.physical(144), 570);
        width.observe(200, 96, true);
        assert_eq!(width.physical(96), 300);
    }
    #[test]
    fn bottom_bar_visible_width_matches_target_instead_of_losing_its_borders() {
        use windows::Win32::Foundation::RECT;
        let outer = RECT {
            left: 473,
            top: 922,
            right: 1353,
            bottom: 953,
        };
        let frame = RECT {
            left: 480,
            top: 922,
            right: 1346,
            bottom: 946,
        };
        let desired = RECT {
            left: 473,
            top: 922,
            right: 1353,
            bottom: 953,
        };
        let bounds = window_bounds_for_frame(&desired, &outer, &frame);
        assert_eq!(
            (bounds.left, bounds.top, bounds.right, bounds.bottom),
            (466, 922, 1360, 960)
        );
        // 加回原生不可见边框后，实际可见的左右端点仍是目标的端点。
        assert_eq!(
            (bounds.left + 7, bounds.right - 7),
            (desired.left, desired.right)
        );
    }
    #[test]
    fn both_sidebar_edges_touch_target_at_each_dpi() {
        use windows::Win32::Foundation::RECT;
        let target = RECT {
            left: 500,
            top: 100,
            right: 1200,
            bottom: 700,
        };
        for border in [0, 7, 11, 14] {
            let outer = RECT {
                left: 0,
                top: 0,
                right: 330,
                bottom: 720,
            };
            let frame = RECT {
                left: border,
                top: 0,
                right: 330 - border,
                bottom: 720 - border,
            };
            for left in [false, true] {
                for width in [330 - border * 2, 400 - border * 2] {
                    let desired = sidebar_bounds(&target, width, left);
                    let bounds = window_bounds_for_frame(&desired, &outer, &frame);
                    let visible_left = bounds.left + border;
                    let visible_right = bounds.right - border;
                    assert_eq!(
                        if left { visible_right } else { visible_left },
                        if left { target.left } else { target.right }
                    );
                    assert_eq!(visible_right - visible_left, width);
                    assert_eq!(
                        (bounds.top, bounds.bottom - border),
                        (target.top, target.bottom)
                    );
                }
            }
        }
    }
    #[test]
    fn vertical_drag_and_resize_keep_sidebar_top_and_bottom_aligned() {
        use windows::Win32::Foundation::RECT;
        let mut target = RECT {
            left: 100,
            top: 100,
            right: 880,
            bottom: 650,
        };
        let first = sidebar_bounds(&target, 330, false);
        assert_eq!((first.left, first.top, first.bottom), (880, 100, 650));
        target.top = 200;
        target.bottom = 750;
        let moved = sidebar_bounds(&target, 330, false);
        assert_eq!((moved.top, moved.bottom), (200, 750));
        target.bottom = 1100;
        let below = sidebar_bounds(&target, 330, false);
        assert_eq!((below.top, below.bottom), (200, 1100));
        target.top = -100;
        target.bottom = 500;
        let above = sidebar_bounds(&target, 330, true);
        assert_eq!((above.left, above.top, above.bottom), (-230, -100, 500));
        target.bottom = 1100;
        let taller = sidebar_bounds(&target, 330, false);
        assert_eq!((taller.top, taller.bottom), (-100, 1100));
        target.top = 1100;
        target.bottom = 1500;
        let outside = sidebar_bounds(&target, 330, false);
        assert_eq!((outside.top, outside.bottom), (1100, 1500));
    }
}
