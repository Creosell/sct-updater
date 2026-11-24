//! Embeds the icon and the file properties of a module launcher into the executable.
//!
//! The release tool passes the values through environment variables:
//! `SCT_ICON` (path of an .ico file), `SCT_TITLE` (module title, shown as the file description),
//! `SCT_EXE` (the name the launcher is installed under) and `SCT_VERSION` (the module release version, shown as
//! the file and product version). Without them the launcher is built plain.

use std::env;

/// Packs up to four numeric parts of a version (`1.6.0`) into the 64-bit value of a Windows file version.
/// Non-numeric parts (such as a `-rc1` suffix) count as zero.
#[cfg(windows)]
fn pack_version(version: &str) -> u64 {
    let mut parts = version
        .split('.')
        .map(|part| part.chars().take_while(char::is_ascii_digit).collect::<String>().parse::<u16>().unwrap_or(0));
    (0..4).fold(0u64, |packed, _| (packed << 16) | u64::from(parts.next().unwrap_or(0)))
}

fn main() {
    for var in ["SCT_ICON", "SCT_TITLE", "SCT_EXE", "SCT_VERSION"] {
        println!("cargo:rerun-if-env-changed={var}");
    }
    if let Ok(icon) = env::var("SCT_ICON") {
        println!("cargo:rerun-if-changed={icon}");
    }

    #[cfg(windows)]
    {
        let mut resource = winresource::WindowsResource::new();
        if let Ok(icon) = env::var("SCT_ICON") {
            resource.set_icon(&icon);
        }
        if let Ok(title) = env::var("SCT_TITLE") {
            resource.set("FileDescription", &title);
        }
        if let Ok(exe) = env::var("SCT_EXE") {
            resource.set("OriginalFilename", &exe);
            resource.set("InternalName", exe.trim_end_matches(".exe"));
        }
        if let Ok(version) = env::var("SCT_VERSION") {
            let packed = pack_version(&version);
            resource.set_version_info(winresource::VersionInfo::FILEVERSION, packed);
            resource.set_version_info(winresource::VersionInfo::PRODUCTVERSION, packed);
            resource.set("FileVersion", &version);
            resource.set("ProductVersion", &version);
            resource.set("Comments", &format!("Launcher {}", env!("CARGO_PKG_VERSION")));
        }
        resource.set("ProductName", "Screen Check Suite");
        resource.set("CompanyName", "Screen Check Suite");
        resource
            .compile()
            .expect("cannot embed the icon and file properties (is the Windows SDK installed?)");
    }
}
