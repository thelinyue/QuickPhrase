//! 更新安装与用户操作互斥。计数覆盖整个异步任务，避免“检查空闲后又开始发送”的竞态。
use std::sync::Mutex;

static ACTIVITY: Mutex<(usize, bool)> = Mutex::new((0, false));
fn enter(state: &mut (usize, bool)) -> Result<(), String> {
    if state.1 {
        return Err("正在退出并安装更新，请稍候。".into());
    }
    state.0 += 1;
    Ok(())
}
fn begin_install(state: &mut (usize, bool)) -> Result<(), String> {
    if state.1 || state.0 != 0 {
        return Err("请等待发送、导入导出和文件传输完成。".into());
    }
    state.1 = true;
    Ok(())
}
pub struct Activity;
impl Activity {
    pub fn enter() -> Result<Self, String> {
        let mut state = ACTIVITY.lock().unwrap();
        enter(&mut state)?;
        Ok(Self)
    }
}
impl Drop for Activity {
    fn drop(&mut self) {
        ACTIVITY.lock().unwrap().0 -= 1;
    }
}
/// 安装启动失败时自动恢复操作；成功后保留排他状态直到进程退出。
pub struct InstallHandoff(bool);
impl InstallHandoff {
    pub fn begin() -> Result<Self, String> {
        let mut state = ACTIVITY.lock().unwrap();
        begin_install(&mut state)?;
        Ok(Self(false))
    }
    pub fn commit(mut self) {
        self.0 = true;
    }
}
impl Drop for InstallHandoff {
    fn drop(&mut self) {
        if !self.0 {
            ACTIVITY.lock().unwrap().1 = false;
        }
    }
}
pub fn ensure_idle() -> Result<(), String> {
    if ACTIVITY.lock().unwrap().1 {
        Err("正在退出并安装更新，请稍候。".into())
    } else {
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn either_operation_or_installer_wins_exclusively() {
        let mut state = (0, false);
        enter(&mut state).unwrap();
        assert!(begin_install(&mut state).is_err());
        assert_eq!(state, (1, false));
        state.0 -= 1;
        begin_install(&mut state).unwrap();
        assert!(enter(&mut state).is_err());
        assert!(begin_install(&mut state).is_err());
        assert_eq!(state, (0, true));
        state.1 = false;
        enter(&mut state).unwrap();
    }
}
