use crate::model::{Block, Result, Segment, Settings};
use serde::{Deserialize, Serialize};
use std::sync::{
    atomic::{AtomicBool, Ordering},
    Arc,
};

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "lowercase")]
pub enum Mode {
    Insert,
    Send,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DeliveryReport {
    pub status: String,
    pub completed_segments: usize,
    pub completed_blocks: usize,
    pub error: Option<String>,
}

/// 进程内所有发送入口共用这把锁。前端禁用按钮只是反馈，真正的防重入在 Rust。
#[derive(Default)]
pub struct DeliveryState {
    pub busy: Arc<AtomicBool>,
    pub cancelled: Arc<AtomicBool>,
}
pub struct DeliveryGuard(Arc<AtomicBool>);
impl Drop for DeliveryGuard {
    fn drop(&mut self) {
        self.0.store(false, Ordering::Release);
    }
}
impl DeliveryState {
    pub fn acquire(&self) -> Result<DeliveryGuard> {
        self.busy
            .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .map_err(|_| "正在处理上一次操作，请等待完成。".to_string())?;
        self.cancelled.store(false, Ordering::Release);
        Ok(DeliveryGuard(self.busy.clone()))
    }
}

/// 原生驱动只负责焦点、剪贴板与输入；顺序和中断规则在此统一，测试驱动可复现失焦等情况。
pub trait DeliveryDriver {
    fn prepare(&mut self) -> Result<()>;
    fn ensure_target(&mut self) -> Result<()>;
    fn paste(&mut self, block: &Block) -> Result<()>;
    fn send_key(&mut self, ctrl: bool) -> Result<()>;
    fn pause(&mut self, millis: u64, cancel: &AtomicBool) -> Result<()>;
    fn finish(&mut self) -> Result<()>;
}

pub fn execute(
    segments: &[Segment],
    mode: Mode,
    settings: &Settings,
    driver: &mut impl DeliveryDriver,
    cancel: &AtomicBool,
) -> DeliveryReport {
    let mut report = DeliveryReport {
        status: "submitted".into(),
        completed_segments: 0,
        completed_blocks: 0,
        error: None,
    };
    let work = (|| -> Result<()> {
        driver.prepare()?;
        for (si, segment) in segments.iter().enumerate() {
            if cancel.load(Ordering::Acquire) {
                return Err("操作已停止，已执行的内容不会自动重发。".into());
            }
            if mode == Mode::Insert && si > 0 {
                driver.ensure_target()?;
                driver.paste(&Block::Text {
                    text: "\r\n".into(),
                })?;
                driver.pause(100, cancel)?;
            }
            for block in &segment.blocks {
                if matches!(block,Block::Text{text} if text.is_empty()) {
                    continue;
                }
                if cancel.load(Ordering::Acquire) {
                    return Err("操作已停止。".into());
                }
                driver.ensure_target()?;
                driver.paste(block)?;
                report.completed_blocks += 1;
                driver.pause(120, cancel)?;
            }
            if mode == Mode::Send {
                driver.ensure_target()?;
                driver.send_key(settings.enter == "ctrl-enter")?;
            }
            report.completed_segments += 1;
            if mode == Mode::Send && si + 1 < segments.len() {
                driver.pause(settings.interval, cancel)?;
            }
        }
        Ok(())
    })();
    if let Err(error) = work {
        report.status = if report.completed_blocks > 0 {
            "interrupted"
        } else {
            "failed"
        }
        .into();
        report.error = Some(error);
    }
    if let Err(error) = driver.finish() {
        report.status = "interrupted".into();
        report.error = Some(match report.error {
            Some(previous) => format!("{previous}；{error}"),
            None => error,
        });
    }
    report
}

#[cfg(test)]
mod tests {
    use super::*;
    struct Fake {
        calls: Vec<String>,
        fail_after: usize,
    }
    impl DeliveryDriver for Fake {
        fn prepare(&mut self) -> Result<()> {
            Ok(())
        }
        fn ensure_target(&mut self) -> Result<()> {
            if self.calls.len() >= self.fail_after {
                Err("目标焦点已变化".into())
            } else {
                Ok(())
            }
        }
        fn paste(&mut self, b: &Block) -> Result<()> {
            self.calls.push(match b {
                Block::Text { text } => text.clone(),
                Block::Image { .. } => "图片".into(),
                Block::File { .. } => "文件".into(),
            });
            Ok(())
        }
        fn send_key(&mut self, ctrl: bool) -> Result<()> {
            self.calls
                .push(if ctrl { "Ctrl+Enter" } else { "Enter" }.into());
            Ok(())
        }
        fn pause(&mut self, _: u64, cancel: &AtomicBool) -> Result<()> {
            if cancel.load(Ordering::Acquire) {
                Err("操作已取消".into())
            } else {
                Ok(())
            }
        }
        fn finish(&mut self) -> Result<()> {
            Ok(())
        }
    }
    fn segments() -> Vec<Segment> {
        vec![
            Segment {
                blocks: vec![
                    Block::Text {
                        text: "文字".into(),
                    },
                    Block::Image {
                        path: "a".into(),
                        name: "a".into(),
                    },
                    Block::File {
                        path: "b".into(),
                        name: "b".into(),
                    },
                ],
            },
            Segment {
                blocks: vec![Block::Text {
                    text: "第二段".into(),
                }],
            },
        ]
    }
    #[test]
    fn send_preserves_order_and_respects_key() {
        let mut d = Fake {
            calls: vec![],
            fail_after: usize::MAX,
        };
        let s = Settings {
            enter: "ctrl-enter".into(),
            ..Settings::default()
        };
        let r = execute(&segments(), Mode::Send, &s, &mut d, &AtomicBool::new(false));
        assert_eq!(
            d.calls,
            vec!["文字", "图片", "文件", "Ctrl+Enter", "第二段", "Ctrl+Enter"]
        );
        assert_eq!(r.completed_segments, 2);
    }
    #[test]
    fn insert_never_sends_and_interrupt_never_retries() {
        let mut d = Fake {
            calls: vec![],
            fail_after: 2,
        };
        let r = execute(
            &segments(),
            Mode::Insert,
            &Settings::default(),
            &mut d,
            &AtomicBool::new(false),
        );
        assert_eq!(r.status, "interrupted");
        assert_eq!(r.completed_segments, 0);
        assert_eq!(d.calls, vec!["文字", "图片"]);
    }
    #[test]
    fn backend_lock_rejects_duplicate_and_releases_on_drop() {
        let s = DeliveryState::default();
        let guard = s.acquire().unwrap();
        assert!(s.acquire().is_err());
        drop(guard);
        assert!(s.acquire().is_ok());
    }
}
