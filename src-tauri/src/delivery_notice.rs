use std::sync::Mutex;

/// 只在本次运行内保存投递结果；按库与账号世代隔离，不保存话术或剪贴板载荷。
#[derive(Clone)]
pub struct Pending {
    pub id: u64,
    pub library_id: String,
    pub session_epoch: u64,
    pub origin: String,
    pub status: String,
    pub message: String,
}

#[derive(Default)]
pub struct DeliveryNotices(Mutex<Vec<Pending>>);

impl DeliveryNotices {
    pub fn push(&self, notice: Pending) {
        self.0.lock().unwrap().push(notice);
    }
    pub fn list(&self, library_id: &str, session_epoch: u64) -> Vec<Pending> {
        self.0
            .lock()
            .unwrap()
            .iter()
            .filter(|item| item.library_id == library_id && item.session_epoch == session_epoch)
            .cloned()
            .collect()
    }
    pub fn dismiss(&self, id: u64, library_id: &str, session_epoch: u64) {
        self.0.lock().unwrap().retain(|item| {
            item.id != id || item.library_id != library_id || item.session_epoch != session_epoch
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn pending_results_are_scoped_and_acknowledged_individually() {
        let notices = DeliveryNotices::default();
        let item = |id, library: &str, epoch| Pending {
            id,
            library_id: library.into(),
            session_epoch: epoch,
            origin: "main".into(),
            status: "interrupted".into(),
            message: "请检查目标".into(),
        };
        notices.push(item(1, "a", 1));
        notices.push(item(2, "a", 1));
        notices.push(item(3, "b", 2));
        notices.dismiss(1, "b", 2);
        assert_eq!(notices.list("a", 1).len(), 2);
        notices.dismiss(1, "a", 1);
        assert_eq!(notices.list("a", 1)[0].id, 2);
        assert_eq!(notices.list("b", 2)[0].id, 3);
        assert!(notices.list("a", 2).is_empty());
    }
}
