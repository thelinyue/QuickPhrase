use crate::model::{Block, Result, Segment, Settings};
use serde::{Deserialize, Serialize};
use std::sync::{
    atomic::{AtomicBool, AtomicU64, Ordering},
    Arc, Mutex,
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
    pub notice_stored: bool,
}

/// 进程内所有发送入口共用这把锁。前端禁用按钮只是反馈，真正的防重入在 Rust。
#[derive(Default)]
pub struct DeliveryState {
    pub busy: Arc<AtomicBool>,
    pub cancelled: Arc<AtomicBool>,
    operation: AtomicU64,
    cancel_lock: Mutex<()>,
}
pub struct DeliveryGuard(
    Arc<AtomicBool>,
    #[allow(dead_code)] crate::update_activity::Activity,
    u64,
);
impl DeliveryGuard {
    pub fn operation(&self) -> u64 {
        self.2
    }
}
impl Drop for DeliveryGuard {
    fn drop(&mut self) {
        self.0.store(false, Ordering::Release);
    }
}
impl DeliveryState {
    pub fn acquire(&self) -> Result<DeliveryGuard> {
        let activity = crate::update_activity::Activity::enter()?;
        let _lock = self.cancel_lock.lock().unwrap();
        self.busy
            .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .map_err(|_| "正在处理上一次操作，请等待完成。".to_string())?;
        self.cancelled.store(false, Ordering::Release);
        let operation = self.operation.fetch_add(1, Ordering::AcqRel) + 1;
        Ok(DeliveryGuard(self.busy.clone(), activity, operation))
    }
    /// 等待提示上的停止按钮绑定原操作，迟到点击不能取消随后开始的投递。
    pub fn cancel(&self, operation: Option<u64>) {
        let _lock = self.cancel_lock.lock().unwrap();
        if operation.is_none_or(|id| {
            self.busy.load(Ordering::Acquire) && self.operation.load(Ordering::Acquire) == id
        }) {
            self.cancelled.store(true, Ordering::Release);
        }
    }
}

/// 原生驱动只负责焦点、剪贴板与输入；顺序和中断规则在此统一，测试驱动可复现失焦等情况。
pub trait DeliveryDriver {
    fn prepare(&mut self) -> Result<()>;
    fn ensure_target(&mut self) -> Result<()>;
    fn paste(&mut self, block: &Block) -> Result<()>;
    fn send_key(&mut self, ctrl: bool) -> Result<()>;
    fn pause(&mut self, millis: u64, cancel: &AtomicBool) -> Result<()>;
    /// 最后一次插入已提交，只等待目标读取剪贴板；保留发送锁，不再因用户接管输入而报中断。
    fn settle(&mut self, millis: u64);
    fn finish(&mut self) -> Result<()>;
    /// 清理触发输入本身也是已执行步骤，即使第一块尚未粘贴也必须报告中断。
    fn input_change(&self) -> Option<&'static str> {
        None
    }
}

pub fn execute(
    segments: &[Segment],
    mode: Mode,
    settings: &Settings,
    driver: &mut impl DeliveryDriver,
    cancel: &AtomicBool,
) -> DeliveryReport {
    let started = std::time::Instant::now();
    let mut stage = 1;
    crate::diagnostics::record(crate::diagnostics::Event::Delivery {
        stage: 0,
        ok: true,
        blocks: 0,
        segments: 0,
        elapsed_ms: 0,
    });
    let mut report = DeliveryReport {
        status: "submitted".into(),
        completed_segments: 0,
        completed_blocks: 0,
        error: None,
        notice_stored: false,
    };
    let work = (|| -> Result<()> {
        driver.prepare()?;
        for (si, segment) in segments.iter().enumerate() {
            if cancel.load(Ordering::Acquire) {
                return Err("操作已停止，已执行的内容不会自动重发。".into());
            }
            if mode == Mode::Insert && si > 0 {
                stage = 2;
                driver.ensure_target()?;
                stage = 3;
                driver.paste(&Block::Text {
                    text: "\r\n".into(),
                })?;
                stage = 4;
                driver.pause(100, cancel)?;
            }
            let mut blocks = segment
                .blocks
                .iter()
                .filter(|block| !matches!(block, Block::Text { text } if text.is_empty()))
                .peekable();
            while let Some(block) = blocks.next() {
                if cancel.load(Ordering::Acquire) {
                    return Err("操作已停止。".into());
                }
                stage = 2;
                driver.ensure_target()?;
                stage = 3;
                driver.paste(block)?;
                report.completed_blocks += 1;
                stage = 4;
                if mode == Mode::Send || blocks.peek().is_some() || si + 1 < segments.len() {
                    driver.pause(120, cancel)?;
                } else {
                    driver.settle(120);
                }
            }
            if mode == Mode::Send {
                stage = 2;
                driver.ensure_target()?;
                stage = 5;
                driver.send_key(settings.enter == "ctrl-enter")?;
            }
            report.completed_segments += 1;
            if mode == Mode::Send && si + 1 < segments.len() {
                stage = 4;
                driver.pause(settings.interval, cancel)?;
            }
        }
        Ok(())
    })();
    if let Err(error) = work {
        report.status = if report.completed_blocks > 0 || driver.input_change().is_some() {
            "interrupted"
        } else {
            "failed"
        }
        .into();
        let error = match driver.input_change() {
            Some(step) => format!("{error}；{step}，不会自动重试或回填。"),
            None => error,
        };
        report.error = Some(if report.completed_blocks > 0 {
            let target = if mode == Mode::Insert {
                "目标输入区"
            } else {
                "目标输入区和已发送内容"
            };
            format!(
                "{error}；已提交{}块内容，请先检查{target}，已执行内容不会自动重发。",
                report.completed_blocks,
            )
        } else {
            error
        });
    }
    if let Err(error) = driver.finish() {
        stage = 6;
        report.status = "interrupted".into();
        report.error = Some(match report.error {
            Some(previous) => format!("{previous}；{error}"),
            None => error,
        });
    }
    crate::diagnostics::record(crate::diagnostics::Event::Delivery {
        stage,
        ok: report.error.is_none(),
        blocks: report.completed_blocks,
        segments: report.completed_segments,
        elapsed_ms: started.elapsed().as_millis() as u64,
    });
    report
}

#[cfg(test)]
mod tests {
    use super::*;
    struct Fake {
        calls: Vec<String>,
        fail_after: usize,
        cancel_after_paste: Option<Arc<AtomicBool>>,
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
            if let Some(cancel) = &self.cancel_after_paste {
                cancel.store(true, Ordering::Release);
            }
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
        fn settle(&mut self, _: u64) {}
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
            cancel_after_paste: None,
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
            cancel_after_paste: None,
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
        assert!(r.error.unwrap().contains("已提交2块内容，请先检查"));
    }
    #[test]
    fn completed_insert_is_not_interrupted_by_following_user_input() {
        let cancel = Arc::new(AtomicBool::new(false));
        let mut driver = Fake {
            calls: vec![],
            fail_after: usize::MAX,
            cancel_after_paste: Some(cancel.clone()),
        };
        let content = [Segment {
            blocks: vec![
                Block::Text {
                    text: "谢谢".into(),
                },
                Block::Text { text: "".into() },
            ],
        }];
        let report = execute(
            &content,
            Mode::Insert,
            &Settings::default(),
            &mut driver,
            &cancel,
        );
        assert!(cancel.load(Ordering::Acquire));
        assert_eq!(report.status, "submitted");
        assert_eq!(report.completed_blocks, 1);
        assert_eq!(report.completed_segments, 1);
        assert!(report.error.is_none());
        assert_eq!(driver.calls, vec!["谢谢"]);
    }

    #[test]
    fn late_stop_from_an_older_wait_cannot_cancel_the_next_operation() {
        let state = DeliveryState::default();
        let first = state.acquire().unwrap();
        let first_id = first.operation();
        drop(first);
        let second = state.acquire().unwrap();
        state.cancel(Some(first_id));
        assert!(!state.cancelled.load(Ordering::Acquire));
        state.cancel(Some(second.operation()));
        assert!(state.cancelled.load(Ordering::Acquire));
    }
    #[test]
    fn input_after_paste_stops_remaining_blocks_segments_and_send() {
        let text = || Block::Text {
            text: "谢谢".into(),
        };
        for (mode, content) in [
            (
                Mode::Send,
                vec![Segment {
                    blocks: vec![text()],
                }],
            ),
            (
                Mode::Insert,
                vec![Segment {
                    blocks: vec![text(), text()],
                }],
            ),
            (
                Mode::Insert,
                vec![
                    Segment {
                        blocks: vec![text()],
                    },
                    Segment {
                        blocks: vec![text()],
                    },
                ],
            ),
        ] {
            let cancel = Arc::new(AtomicBool::new(false));
            let mut driver = Fake {
                calls: vec![],
                fail_after: usize::MAX,
                cancel_after_paste: Some(cancel.clone()),
            };
            let report = execute(&content, mode, &Settings::default(), &mut driver, &cancel);
            assert_eq!(report.status, "interrupted");
            assert_eq!(report.completed_blocks, 1);
            assert_eq!(report.completed_segments, 0);
            assert_eq!(driver.calls, vec!["谢谢"]);
        }
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
