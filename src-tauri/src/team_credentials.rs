//! 会话只保存在 Windows 凭据管理器，不进入 SQLite、设置、前端或日志。
use crate::model::Result;

#[cfg(windows)]
pub fn write(partition: &str, token: &str) -> Result<()> {
    use windows::{core::PWSTR, Win32::Security::Credentials::*};
    let mut target: Vec<u16> = format!("Shanyu/teams/{partition}")
        .encode_utf16()
        .chain(Some(0))
        .collect();
    let mut bytes = token.as_bytes().to_vec();
    let credential = CREDENTIALW {
        Type: CRED_TYPE_GENERIC,
        TargetName: PWSTR(target.as_mut_ptr()),
        CredentialBlobSize: bytes.len() as u32,
        CredentialBlob: bytes.as_mut_ptr(),
        Persist: CRED_PERSIST_LOCAL_MACHINE,
        ..Default::default()
    };
    unsafe { CredWriteW(&credential, 0) }
        .map_err(|_| "无法将登录凭据写入 Windows 凭据管理器。".into())
}
#[cfg(windows)]
pub fn read(partition: &str) -> Result<Option<String>> {
    use windows::{
        core::PCWSTR,
        Win32::{Foundation::ERROR_NOT_FOUND, Security::Credentials::*},
    };
    let target: Vec<u16> = format!("Shanyu/teams/{partition}")
        .encode_utf16()
        .chain(Some(0))
        .collect();
    let mut credential = std::ptr::null_mut();
    match unsafe {
        CredReadW(
            PCWSTR(target.as_ptr()),
            CRED_TYPE_GENERIC,
            None,
            &mut credential,
        )
    } {
        Ok(()) => unsafe {
            let bytes = std::slice::from_raw_parts(
                (*credential).CredentialBlob,
                (*credential).CredentialBlobSize as usize,
            );
            let value = String::from_utf8(bytes.to_vec())
                .map_err(|_| "Windows 登录凭据无法读取。".to_owned());
            CredFree(credential.cast());
            value.map(Some)
        },
        Err(e) if e.code() == ERROR_NOT_FOUND.to_hresult() => Ok(None),
        Err(_) => Err("无法读取 Windows 凭据管理器。".into()),
    }
}
#[cfg(windows)]
pub fn delete(partition: &str) -> Result<()> {
    use windows::{
        core::PCWSTR,
        Win32::{Foundation::ERROR_NOT_FOUND, Security::Credentials::*},
    };
    let target: Vec<u16> = format!("Shanyu/teams/{partition}")
        .encode_utf16()
        .chain(Some(0))
        .collect();
    match unsafe { CredDeleteW(PCWSTR(target.as_ptr()), CRED_TYPE_GENERIC, None) } {
        Ok(()) => Ok(()),
        Err(e) if e.code() == ERROR_NOT_FOUND.to_hresult() => Ok(()),
        Err(_) => Err("无法清除 Windows 登录凭据。".into()),
    }
}
#[cfg(not(windows))]
pub fn write(_: &str, _: &str) -> Result<()> {
    Err("共享话术登录凭据需要 Windows 凭据管理器。".into())
}
#[cfg(not(windows))]
pub fn read(_: &str) -> Result<Option<String>> {
    Ok(None)
}
#[cfg(not(windows))]
pub fn delete(_: &str) -> Result<()> {
    Ok(())
}
