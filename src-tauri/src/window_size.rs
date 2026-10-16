//! 主窗口尺寸属于本机，按客户区 DIP 保存；独立于账号、数据目录迁移及安装目录。
//! 只观察正常窗口，贴边移动不改变客户区，最小化和最大化的临时尺寸不得覆盖记录。
use crate::model::Result;
use serde::{Deserialize, Serialize};
use std::{
    io::Write,
    path::PathBuf,
    time::{Duration, Instant},
};

#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
pub struct Size {
    pub width: f64,
    pub height: f64,
}
impl Size {
    pub fn from_physical(width: i32, height: i32, dpi: u32) -> Option<Self> {
        let scale = 96.0 / f64::from(dpi.max(96));
        let size = Self {
            width: f64::from(width) * scale,
            height: f64::from(height) * scale,
        };
        size.valid().then_some(size)
    }
    fn valid(self) -> bool {
        self.width.is_finite() && self.height.is_finite() && self.width > 0.0 && self.height > 0.0
    }
}

/// 拖动结束后等待尺寸稳定再落盘；退出时直接保存最新观测值，避免升级关闭丢掉最后一次调整。
#[derive(Default)]
pub struct WindowSize {
    path: Option<PathBuf>,
    pub current: Option<Size>,
    pub restore_pending: bool,
    saved: Option<Size>,
    changed: Option<Instant>,
}
impl WindowSize {
    pub fn load(&mut self, directory: PathBuf) -> Result<Option<Size>> {
        let path = directory.join("window-size.json");
        self.path = Some(path.clone());
        let text = match std::fs::read(&path) {
            Ok(text) => text,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
            Err(error) => return Err(format!("无法读取上次窗口大小：{error}")),
        };
        // 安装器迁移旧版尺寸时使用 Windows UTF-8 文件，兼容可选的 BOM。
        let text = text.strip_prefix(&[0xef, 0xbb, 0xbf]).unwrap_or(&text);
        let size: Size =
            serde_json::from_slice(text).map_err(|_| "窗口大小记录损坏，将使用默认大小。")?;
        if !size.valid() {
            return Err("窗口大小记录无效，将使用默认大小。".into());
        }
        self.current = Some(size);
        self.restore_pending = true;
        self.saved = Some(size);
        Ok(Some(size))
    }
    pub fn observe(&mut self, size: Size, now: Instant) -> bool {
        if self.current != Some(size) {
            self.current = Some(size);
            self.changed = Some(now);
        }
        self.saved != self.current
            && self
                .changed
                .is_some_and(|changed| now.duration_since(changed) >= Duration::from_millis(250))
    }
    pub fn persist(&mut self) -> Result<()> {
        let (Some(path), Some(size)) = (&self.path, self.current) else {
            return Ok(());
        };
        if self.saved == Some(size) {
            return Ok(());
        }
        // 同一尺寸失败只提示一次；后续调整仍可重试，避免磁盘故障时每轮跟随都刷屏。
        self.saved = Some(size);
        let write = || -> std::result::Result<(), Box<dyn std::error::Error>> {
            std::fs::create_dir_all(path.parent().ok_or("窗口大小保存目录无效")?)?;
            let mut file = tempfile::NamedTempFile::new_in(path.parent().unwrap())?;
            file.write_all(&serde_json::to_vec(&size)?)?;
            file.persist(path)?;
            Ok(())
        };
        write().map_err(|error| format!("无法保存窗口大小，下次启动可能恢复默认大小：{error}"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn size_survives_restart_and_changes_are_saved_only_after_settling() {
        let dir = tempfile::tempdir().unwrap();
        let mut state = WindowSize::default();
        assert_eq!(state.load(dir.path().into()).unwrap(), None);
        let t = Instant::now();
        let size = Size::from_physical(630, 960, 144).unwrap();
        assert_eq!(
            size,
            Size {
                width: 420.0,
                height: 640.0
            }
        );
        assert!(!state.observe(size, t));
        assert!(state.observe(size, t + Duration::from_millis(250)));
        state.persist().unwrap();
        assert_eq!(
            WindowSize::default().load(dir.path().into()).unwrap(),
            Some(size)
        );
        let resized = Size::from_physical(800, 1400, 192).unwrap();
        state.observe(resized, t + Duration::from_secs(1));
        state.persist().unwrap(); // 退出不必等待250毫秒。
        assert_eq!(
            WindowSize::default().load(dir.path().into()).unwrap(),
            Some(resized)
        );
        assert!(Size::from_physical(0, 0, 96).is_none());
        assert!(Size::from_physical(-1, 400, 96).is_none());
    }
    #[test]
    fn installer_seed_and_invalid_record_are_handled() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("window-size.json");
        std::fs::write(&path, "\u{feff}{\"width\":420,\"height\":640}").unwrap();
        assert_eq!(
            WindowSize::default().load(dir.path().into()).unwrap(),
            Some(Size {
                width: 420.0,
                height: 640.0
            })
        );
        std::fs::write(&path, "{\"width\":0,\"height\":640}").unwrap();
        assert!(WindowSize::default().load(dir.path().into()).is_err());
    }
}
