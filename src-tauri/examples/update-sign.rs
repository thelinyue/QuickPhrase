//! 发布工具只从环境变量读取私钥和密码，不输出秘密。客户端仅包含公钥与验证器。
use std::{env, fs, io::Cursor, path::Path};
fn main() -> Result<(), Box<dyn std::error::Error>> {
    let args: Vec<_> = env::args().collect();
    match args.get(1).map(String::as_str) {
        Some("generate") if args.len() == 3 => {
            let dir = Path::new(&args[2]);
            if dir.join("update.key").exists() {
                return Err("私钥已存在，不能覆盖。".into());
            }
            let password = env::var("QUICKPHRASE_UPDATE_KEY_PASSWORD")
                .map_err(|_| "缺少签名密码环境变量。")?;
            let pair = minisign::KeyPair::generate_encrypted_keypair(Some(password))?;
            fs::write(dir.join("update.key"), pair.sk.to_box(None)?.to_bytes())?;
            fs::write(dir.join("update.pub"), pair.pk.to_box()?.to_bytes())?;
        }
        Some("sign") if args.len() == 4 => {
            let secret =
                env::var("QUICKPHRASE_UPDATE_PRIVATE_KEY").map_err(|_| "缺少更新签名私钥。")?;
            let password =
                env::var("QUICKPHRASE_UPDATE_KEY_PASSWORD").map_err(|_| "缺少更新签名密码。")?;
            let key = minisign::SecretKey::from_box(secret.into(), Some(password))
                .map_err(|_| "更新签名私钥或密码无效。")?;
            let public = minisign::PublicKey::from_file(&args[3])?;
            let data = fs::read(&args[2])?;
            let signature = minisign::sign(Some(&public), &key, Cursor::new(&data), None, None)?;
            minisign::verify(&public, &signature, Cursor::new(&data), true, false, false)?;
            fs::write(format!("{}.minisig", args[2]), signature.to_string())?;
        }
        _ => {
            return Err(
                "用法：update-sign generate <密钥目录> 或 sign <校验清单> <公钥文件>".into(),
            )
        }
    }
    Ok(())
}
