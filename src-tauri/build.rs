fn main() {
    // PNG 用于窗口和托盘，ICO 用于 EXE；图标变化时重新生成资源，避免沿用缓存中的旧图标。
    println!("cargo:rerun-if-changed=icons/icon.png");
    println!("cargo:rerun-if-changed=icons/icon.ico");
    tauri_build::build()
}
