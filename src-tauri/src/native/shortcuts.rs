use crate::model::Result;
use std::{
    collections::HashSet,
    time::{Duration, Instant},
};

pub const SEQUENCE_TIMEOUT: Duration = Duration::from_secs(1);

/// 沿用字符串存储，兼容已有单组合键；两个普通键表示先后按下，而非同时按住。
#[derive(Debug, Clone, PartialEq)]
pub struct Shortcut {
    pub modifiers: u32,
    pub key: u32,
    pub next_key: Option<u32>,
}
impl Shortcut {
    /// 可输入首键立即交给输入法；即使后接 F 键，也不能因共享首键而延迟普通输入。
    /// Ctrl／Alt／Win 组合仍保留原来的拦截语义，Shift 允许正常输入大写字母。
    pub fn passes_input(&self) -> bool {
        self.next_key.is_some()
            && self.modifiers & (1 | 2 | 8) == 0
            && ((0x30..=0x39).contains(&self.key) || (0x41..=0x5A).contains(&self.key))
    }
    pub fn canonical(&self) -> String {
        let mut parts = [(1, "Ctrl"), (2, "Alt"), (4, "Shift"), (8, "Win")]
            .into_iter()
            .filter(|(bit, _)| self.modifiers & bit != 0)
            .map(|(_, name)| name.to_owned())
            .collect::<Vec<_>>();
        for key in std::iter::once(self.key).chain(self.next_key) {
            parts.push(if (0x70..=0x87).contains(&key) {
                format!("F{}", key - 0x70 + 1)
            } else {
                char::from_u32(key).unwrap().to_string()
            });
        }
        parts.join("+")
    }
    /// 同一首键的不同双键序列可共存；单键与其双键前缀不可共存，否则后者永远无法触发。
    pub fn conflicts(&self, other: &Self) -> bool {
        self.modifiers == other.modifiers
            && self.key == other.key
            && (self.next_key == other.next_key
                || self.next_key.is_none()
                || other.next_key.is_none())
    }
    /// 完全同键可共享候选；仅拒绝单组合键与双键序列之间无法消解的前缀歧义。
    pub fn prefix_conflicts(&self, other: &Self) -> bool {
        self != other && self.conflicts(other)
    }
}
pub fn parse_shortcut(text: &str) -> Result<Shortcut> {
    let mut modifiers = 0;
    let mut keys = vec![];
    for part in text.split('+').map(str::trim) {
        let bit = match part.to_ascii_lowercase().as_str() {
            "ctrl" | "control" => Some(1),
            "alt" => Some(2),
            "shift" => Some(4),
            "win" | "meta" => Some(8),
            _ => None,
        };
        if let Some(bit) = bit {
            if !keys.is_empty() {
                return Err("修饰键应放在字母、数字或F键之前。".into());
            }
            if modifiers & bit != 0 {
                return Err("快捷键包含重复修饰键。".into());
            }
            modifiers |= bit;
        } else {
            let key = part.to_ascii_uppercase();
            let vk = if key.len() == 1 && key.as_bytes()[0].is_ascii_alphanumeric() {
                key.as_bytes()[0] as u32
            } else if let Some(n) = key
                .strip_prefix('F')
                .and_then(|v| v.parse::<u32>().ok())
                .filter(|n| (1..=24).contains(n))
            {
                0x70 + n - 1
            } else {
                return Err("请使用字母、数字或F1—F24设置快捷键。".into());
            };
            keys.push(vk);
        }
    }
    if keys.is_empty() || keys.len() > 2 {
        return Err("快捷键需要一个或两个普通键，例如 Ctrl+1 或 Ctrl+Y+C。".into());
    }
    if modifiers == 0 && keys.len() != 2 {
        return Err("无修饰键时需要依次按两个键，例如 1+1。".into());
    }
    Ok(Shortcut {
        modifiers,
        key: keys[0],
        next_key: keys.get(1).copied(),
    })
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct KeyEvent {
    pub key: u32,
    pub down: bool,
    pub extended: bool,
}
#[derive(Default, Debug)]
pub struct KeyAction {
    pub swallowed: bool,
    pub replay: Vec<KeyEvent>,
    pub fired: Option<String>,
    pub discarded: bool,
    /// 只记录已透传的触发输入，不把它们作为失败序列回放。
    pub input_started: bool,
    pub observing_input: bool,
    pub passed_input: Option<Vec<KeyEvent>>,
}
struct Pending {
    context: (i64, i64),
    modifiers: u32,
    key: u32,
    started: Instant,
    events: Vec<KeyEvent>,
    passes_input: bool,
}

/// 可输入双键只观察匹配、不截留或回放；已透传事件供候选核验替换范围。
/// 功能组合仍暂存首键一秒，失败或超时只在原输入位置回放。
/// 原生窗口或输入控件变化时取消且不向新焦点回放；长按重复、合成事件不会重复触发。
#[derive(Default)]
pub struct ShortcutMatcher {
    generation: u64,
    pending: Option<Pending>,
    held: HashSet<u32>,
    consumed: HashSet<u32>,
    submit_key: Option<u32>,
}
impl ShortcutMatcher {
    /// 实际内容或授权变化使未完成序列失效；仍在原输入区时回放暂存首键。
    pub fn rebind(&mut self, generation: u64, context: Option<(i64, i64)>) -> KeyAction {
        if generation == self.generation {
            return KeyAction::default();
        }
        self.generation = generation;
        self.flush(context)
    }
    pub fn consume(&mut self, key: u32) {
        self.consumed.insert(key);
        self.submit_key = Some(key);
    }
    /// WebView 失焦或按钮禁用时可能收不到松键消息，按系统键态解除界面提交键的消费。
    pub fn release_submit_key(&mut self, down: impl FnOnce(u32) -> bool) {
        if let Some(key) = self.submit_key {
            if !down(key) {
                self.consumed.remove(&key);
                self.held.remove(&key);
                self.submit_key = None;
            }
        }
    }
    fn flush(&mut self, context: Option<(i64, i64)>) -> KeyAction {
        let Some(pending) = self.pending.take() else {
            return KeyAction::default();
        };
        if context == Some(pending.context) {
            KeyAction {
                replay: if pending.passes_input {
                    vec![]
                } else {
                    pending.events
                },
                ..Default::default()
            }
        } else {
            KeyAction {
                discarded: true,
                ..Default::default()
            }
        }
    }
    pub fn tick(&mut self, context: Option<(i64, i64)>, now: Instant) -> KeyAction {
        if self.pending.as_ref().is_some_and(|p| {
            context != Some(p.context) || now.duration_since(p.started) >= SEQUENCE_TIMEOUT
        }) {
            return self.flush(context);
        }
        KeyAction::default()
    }
    pub fn event(
        &mut self,
        event: KeyEvent,
        context: Option<(i64, i64)>,
        modifiers: u32,
        bindings: &[(String, Shortcut)],
        now: Instant,
    ) -> KeyAction {
        let mut action = self.tick(context, now);
        let repeat = if event.down {
            !self.held.insert(event.key)
        } else {
            self.held.remove(&event.key);
            false
        };
        if self.consumed.contains(&event.key) {
            if !event.down {
                self.consumed.remove(&event.key);
            }
            action.swallowed = true;
            return action;
        }
        let Some(context) = context else {
            return action;
        };
        if let Some(pending) = &mut self.pending {
            if event.key == pending.key && (!event.down || repeat) {
                pending.events.push(event);
                action.observing_input = pending.passes_input;
                if !pending.passes_input {
                    action.swallowed = true;
                }
                return action;
            }
            if event.down && !repeat && modifiers == pending.modifiers {
                if let Some((id, _)) = bindings.iter().find(|(_, s)| {
                    s.modifiers == pending.modifiers
                        && s.key == pending.key
                        && s.next_key == Some(event.key)
                }) {
                    // 普通输入的按下和松开都放行；仅拦截型组合消费尚未松开的触发键。
                    if !pending.passes_input {
                        if self.held.contains(&pending.key) {
                            self.consumed.insert(pending.key);
                        }
                        self.consumed.insert(event.key);
                        action.swallowed = true;
                    } else {
                        pending.events.push(event);
                        action.passed_input = Some(pending.events.clone());
                    }
                    self.pending = None;
                    action.fired = Some(id.clone());
                    return action;
                }
            }
            // 其他按键（含松开修饰键）结束匹配；仅还原拦截的首键，不把该事件重新解释为快捷键。
            let flushed = self.flush(Some(context));
            action.replay.extend(flushed.replay);
            return action;
        }
        if !action.replay.is_empty() || !event.down || repeat {
            return action;
        }
        if let Some((id, shortcut)) = bindings
            .iter()
            .find(|(_, s)| s.key == event.key && s.modifiers == modifiers)
        {
            action.swallowed = !shortcut.passes_input();
            if shortcut.next_key.is_some() {
                action.input_started = shortcut.passes_input();
                self.pending = Some(Pending {
                    context,
                    modifiers,
                    key: event.key,
                    started: now,
                    events: vec![event],
                    passes_input: shortcut.passes_input(),
                });
            } else {
                self.consumed.insert(event.key);
                action.fired = Some(id.clone());
            }
        }
        action
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    const CONTEXT: Option<(i64, i64)> = Some((10, 11));
    fn event(key: char, down: bool) -> KeyEvent {
        KeyEvent {
            key: key as u32,
            down,
            extended: false,
        }
    }
    fn bindings(values: &[&str]) -> Vec<(String, Shortcut)> {
        values
            .iter()
            .enumerate()
            .map(|(i, text)| (i.to_string(), parse_shortcut(text).unwrap()))
            .collect()
    }
    #[test]
    fn parse_sequences_preserves_old_chords_and_canonical_uniqueness() {
        assert_eq!(
            parse_shortcut("alt+Control+1").unwrap().canonical(),
            "Ctrl+Alt+1"
        );
        assert_eq!(parse_shortcut("Ctrl+y+c").unwrap().canonical(), "Ctrl+Y+C");
        assert_eq!(parse_shortcut("1+1").unwrap().canonical(), "1+1");
        assert_eq!(parse_shortcut("F1+F24").unwrap().canonical(), "F1+F24");
        for value in [
            "",
            "A",
            "Ctrl",
            "Ctrl+",
            "Ctrl+Y+C+D",
            "Ctrl+Ctrl+1",
            "Ctrl+F25",
            "1+Alt+2",
        ] {
            assert!(parse_shortcut(value).is_err(), "{value}");
        }
        assert!(parse_shortcut("Ctrl+Y")
            .unwrap()
            .conflicts(&parse_shortcut("Ctrl+Y+C").unwrap()));
        assert!(!parse_shortcut("Ctrl+Y+C")
            .unwrap()
            .conflicts(&parse_shortcut("Ctrl+Y+D").unwrap()));
    }
    #[test]
    fn ui_submit_key_remains_consumed_after_target_focus_changes_until_release() {
        let mut matcher = ShortcutMatcher::default();
        let now = Instant::now();
        let enter = KeyEvent {
            key: 0x0D,
            down: true,
            extended: false,
        };
        matcher.event(enter, CONTEXT, 0, &[], now);
        matcher.consume(enter.key);
        for _ in 0..5 {
            assert!(matcher.event(enter, Some((20, 21)), 0, &[], now).swallowed);
        }
        assert!(
            matcher
                .event(
                    KeyEvent {
                        down: false,
                        ..enter
                    },
                    None,
                    0,
                    &[],
                    now
                )
                .swallowed
        );
        assert!(!matcher.event(enter, CONTEXT, 0, &[], now).swallowed);
        matcher.consume(enter.key);
        matcher.release_submit_key(|_| true);
        assert!(matcher.event(enter, CONTEXT, 0, &[], now).swallowed);
        matcher.release_submit_key(|_| false);
        assert!(!matcher.event(enter, CONTEXT, 0, &[], now).swallowed);
    }
    #[test]
    fn ordered_keys_fire_only_after_first_release_and_swallow_both_keys() {
        let mut matcher = ShortcutMatcher::default();
        let now = Instant::now();
        let bindings = bindings(&["Ctrl+Y+C"]);
        let prefix = matcher.event(event('Y', true), CONTEXT, 1, &bindings, now);
        assert!(prefix.swallowed);
        assert!(prefix.fired.is_none());
        assert!(
            matcher
                .event(event('Y', false), CONTEXT, 1, &bindings, now)
                .swallowed
        );
        let complete = matcher.event(event('C', true), CONTEXT, 1, &bindings, now);
        assert_eq!(complete.fired.as_deref(), Some("0"));
        assert!(complete.replay.is_empty());
        assert!(complete.swallowed);
        assert!(matcher
            .event(event('C', true), CONTEXT, 1, &bindings, now)
            .fired
            .is_none());
        assert!(
            matcher
                .event(event('C', false), CONTEXT, 1, &bindings, now)
                .swallowed
        );
    }
    #[test]
    fn overlapping_ordered_keys_fire_once_and_preserve_input_policy() {
        for (binding, modifiers) in [("A+B", 0), ("Shift+A+B", 4), ("Ctrl+A+B", 1)] {
            let mut matcher = ShortcutMatcher::default();
            let now = Instant::now();
            let bindings = bindings(&[binding]);
            assert_eq!(
                matcher
                    .event(event('A', true), CONTEXT, modifiers, &bindings, now)
                    .swallowed,
                modifiers == 1
            );
            let second = matcher.event(event('B', true), CONTEXT, modifiers, &bindings, now);
            assert_eq!(second.fired.as_deref(), Some("0"));
            assert_eq!(second.swallowed, modifiers == 1);
            assert!(second.replay.is_empty());
            for key in ['A', 'B'] {
                let repeat = matcher.event(event(key, true), CONTEXT, modifiers, &bindings, now);
                assert_eq!(repeat.swallowed, modifiers == 1);
                assert!(repeat.fired.is_none());
                assert_eq!(
                    matcher
                        .event(event(key, false), CONTEXT, modifiers, &bindings, now)
                        .swallowed,
                    modifiers == 1
                );
            }
        }
    }

    #[test]
    fn consumed_keys_require_real_release_even_after_context_change() {
        let mut matcher = ShortcutMatcher::default();
        let now = Instant::now();
        let keys = bindings(&["Ctrl+1+1"]);
        matcher.event(event('1', true), CONTEXT, 1, &keys, now);
        matcher.event(event('1', false), CONTEXT, 1, &keys, now);
        assert!(matcher
            .event(event('1', true), CONTEXT, 1, &keys, now)
            .fired
            .is_some());
        matcher.tick(None, now + SEQUENCE_TIMEOUT);
        for context in [None, CONTEXT] {
            let action = matcher.event(event('1', true), context, 1, &keys, now);
            assert!(action.swallowed);
            assert!(action.fired.is_none());
        }
        assert!(
            matcher
                .event(event('1', false), None, 0, &keys, now)
                .swallowed
        );
        matcher.event(event('1', true), CONTEXT, 1, &keys, now);
        matcher.event(event('1', false), CONTEXT, 1, &keys, now);
        assert!(matcher
            .event(event('1', true), CONTEXT, 1, &keys, now)
            .fired
            .is_some());
    }

    #[test]
    fn repeated_key_sequence_requires_two_distinct_presses() {
        let mut matcher = ShortcutMatcher::default();
        let now = Instant::now();
        let bindings = bindings(&["1+1"]);
        matcher.event(event('1', true), CONTEXT, 0, &bindings, now);
        assert!(matcher
            .event(event('1', true), CONTEXT, 0, &bindings, now)
            .fired
            .is_none());
        matcher.event(event('1', false), CONTEXT, 0, &bindings, now);
        assert_eq!(
            matcher
                .event(event('1', true), CONTEXT, 0, &bindings, now)
                .fired
                .as_deref(),
            Some("0")
        );
        assert!(
            !matcher
                .event(event('1', false), CONTEXT, 0, &bindings, now)
                .swallowed
        );
    }
    #[test]
    fn wrong_second_key_timeout_and_modifier_release_restore_original_events() {
        for next in [
            event('X', true),
            KeyEvent {
                key: 0x11,
                down: false,
                extended: false,
            },
        ] {
            let mut matcher = ShortcutMatcher::default();
            let now = Instant::now();
            let bindings = bindings(&["Ctrl+Y+C"]);
            matcher.event(event('Y', true), CONTEXT, 1, &bindings, now);
            matcher.event(event('Y', false), CONTEXT, 1, &bindings, now);
            let fail = matcher.event(next, CONTEXT, 1, &bindings, now);
            assert_eq!(fail.replay, vec![event('Y', true), event('Y', false)]);
            assert!(fail.fired.is_none());
            assert!(!fail.swallowed);
        }
        let mut matcher = ShortcutMatcher::default();
        let now = Instant::now();
        let bindings = bindings(&["Ctrl+1+1"]);
        matcher.event(event('1', true), CONTEXT, 1, &bindings, now);
        matcher.event(event('1', true), CONTEXT, 1, &bindings, now);
        matcher.event(event('1', false), CONTEXT, 1, &bindings, now);
        let timed = matcher.tick(CONTEXT, now + SEQUENCE_TIMEOUT);
        assert_eq!(
            timed.replay,
            vec![event('1', true), event('1', true), event('1', false)]
        );
        assert!(timed.fired.is_none());
    }
    #[test]
    fn new_focus_or_disabled_scope_cancels_without_writing_to_new_input() {
        for context in [Some((20, 21)), Some((10, 12)), None] {
            let mut matcher = ShortcutMatcher::default();
            let now = Instant::now();
            let bindings = bindings(&["1+1"]);
            matcher.event(event('1', true), CONTEXT, 0, &bindings, now);
            matcher.event(event('1', false), CONTEXT, 0, &bindings, now);
            let cancelled = matcher.tick(context, now);
            assert!(cancelled.discarded);
            assert!(cancelled.replay.is_empty());
            assert!(cancelled.fired.is_none());
            assert!(matcher
                .event(event('1', true), context, 0, &bindings, now)
                .fired
                .is_none());
        }
    }
    #[test]
    fn shared_prefix_selects_correct_phrase_and_deleted_bindings_cannot_fire() {
        let mut matcher = ShortcutMatcher::default();
        let now = Instant::now();
        let all = bindings(&["Y+C", "Y+D"]);
        matcher.event(event('Y', true), CONTEXT, 0, &all, now);
        matcher.event(event('Y', false), CONTEXT, 0, &all, now);
        assert_eq!(
            matcher
                .event(event('D', true), CONTEXT, 0, &all, now)
                .fired
                .as_deref(),
            Some("1")
        );
        matcher.event(event('D', false), CONTEXT, 0, &all, now);
        matcher.event(event('Y', true), CONTEXT, 0, &all, now);
        matcher.event(event('Y', false), CONTEXT, 0, &all, now);
        let deleted = matcher.event(event('C', true), CONTEXT, 0, &[], now);
        assert!(deleted.fired.is_none());
        assert!(deleted.replay.is_empty());
    }

    #[test]
    fn ordinary_prefix_is_immediate_and_never_replayed_on_failure_or_rebind() {
        for ending in 0..4 {
            let mut matcher = ShortcutMatcher::default();
            let now = Instant::now();
            let keys = bindings(&["Q+W", "Q+F1"]);
            for event in [event('Q', true), event('Q', true), event('Q', false)] {
                let action = matcher.event(event, CONTEXT, 0, &keys, now);
                assert!(!action.swallowed);
                assert!(action.replay.is_empty());
                assert!(action.fired.is_none());
            }
            let action = match ending {
                0 => matcher.event(event('X', true), CONTEXT, 0, &keys, now),
                1 => matcher.tick(CONTEXT, now + SEQUENCE_TIMEOUT),
                2 => matcher.rebind(1, CONTEXT),
                _ => matcher.tick(Some((10, 12)), now),
            };
            assert!(!action.swallowed);
            assert!(action.replay.is_empty());
            assert!(action.fired.is_none());
            assert!(matcher
                .event(event('W', true), CONTEXT, 0, &keys, now)
                .fired
                .is_none());
        }
    }
    #[test]
    fn matched_input_includes_actual_prefix_repeats_without_replaying() {
        let mut matcher = ShortcutMatcher::default();
        let now = Instant::now();
        let keys = bindings(&["1+2"]);
        let mut passed = vec![];
        for event in [
            event('1', true),
            event('1', true),
            event('1', false),
            event('2', true),
        ] {
            passed.push(event);
            let action = matcher.event(event, CONTEXT, 0, &keys, now);
            assert!(!action.swallowed);
            assert!(action.replay.is_empty());
            if action.fired.is_some() {
                assert_eq!(action.passed_input.unwrap(), passed);
            }
        }
    }
}
