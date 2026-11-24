//! Starts a Python module of the Screen Check Suite from the shared embeddable Python.
//!
//! Layout next to the executable (the Updater root):
//! `apps/python_runtime/python.exe` and `apps/<app>/__main__.py`, where `<app>` is fixed at build time through
//! the `SCT_APP` environment variable. The launcher passes its arguments through, keeps the working directory
//! of the caller, exposes the Updater root to the module as `SCT_ROOT` and returns the exit code of the module.

use std::env;
use std::path::{Path, PathBuf};
use std::process::{exit, Command};

const APP: &str = match option_env!("SCT_APP") {
    Some(name) => name,
    None => "app",
};

const ROOT_ENV_VAR: &str = "SCT_ROOT";

/// Locations of the interpreter and of the module folder below the Updater root.
#[derive(Debug, PartialEq)]
struct Layout {
    python: PathBuf,
    entry: PathBuf,
}

/// Builds the interpreter and module paths for an Updater root and an application name.
fn layout(root: &Path, app: &str) -> Layout {
    Layout {
        python: root.join("apps").join("python_runtime").join("python.exe"),
        entry: root.join("apps").join(app),
    }
}

/// Checks that the interpreter and the module folder exist and explains what to do when they do not.
fn validate(layout: &Layout) -> Result<(), String> {
    if !layout.python.is_file() {
        return Err(format!(
            "Python runtime not found: {}\nRun the Updater to install or repair the module.",
            layout.python.display()
        ));
    }
    if !layout.entry.join("__main__.py").is_file() {
        return Err(format!(
            "Module not found: {}\nRun the Updater to install or repair the module.",
            layout.entry.display()
        ));
    }
    Ok(())
}

#[cfg(windows)]
mod console {
    extern "system" {
        fn SetConsoleCtrlHandler(handler: Option<extern "system" fn(u32) -> i32>, add: i32) -> i32;
    }

    extern "system" fn ignore(_signal: u32) -> i32 {
        1
    }

    /// Keeps the launcher alive on Ctrl+C: the module in the same console still receives it and exits,
    /// and the launcher then returns the module's exit code instead of vanishing first.
    pub fn survive_ctrl_c() {
        unsafe {
            SetConsoleCtrlHandler(Some(ignore), 1);
        }
    }
}

#[cfg(not(windows))]
mod console {
    pub fn survive_ctrl_c() {}
}

/// Runs the module with the arguments of the launcher and returns its exit code.
fn run() -> Result<i32, String> {
    let exe = env::current_exe().map_err(|e| format!("cannot resolve the launcher path: {e}"))?;
    let root = exe.parent().ok_or("the launcher has no parent directory")?;

    let layout = layout(root, APP);
    validate(&layout)?;

    console::survive_ctrl_c();
    let status = Command::new(&layout.python)
        .arg("-X")
        .arg("utf8")
        .arg(&layout.entry)
        .args(env::args_os().skip(1))
        .env(ROOT_ENV_VAR, root)
        .status()
        .map_err(|e| format!("cannot start {}: {e}", layout.python.display()))?;

    Ok(status.code().unwrap_or(1))
}

fn main() {
    match run() {
        Ok(code) => exit(code),
        Err(message) => {
            eprintln!("{message}");
            exit(1);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    #[test]
    fn layout_points_below_the_updater_root() {
        let result = layout(Path::new("C:/SCT"), "report_generator");

        assert_eq!(result.python, Path::new("C:/SCT/apps/python_runtime/python.exe"));
        assert_eq!(result.entry, Path::new("C:/SCT/apps/report_generator"));
    }

    #[test]
    fn validate_reports_a_missing_runtime() {
        let root = env::temp_dir().join("sct_launcher_test_missing_runtime");
        let _ = fs::remove_dir_all(&root);

        let error = validate(&layout(&root, "demo")).unwrap_err();

        assert!(error.contains("Python runtime not found"));
        assert!(error.contains("Updater"));
    }

    #[test]
    fn validate_reports_a_missing_module() {
        let root = env::temp_dir().join("sct_launcher_test_missing_module");
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(root.join("apps").join("python_runtime")).unwrap();
        fs::write(root.join("apps").join("python_runtime").join("python.exe"), b"").unwrap();

        let error = validate(&layout(&root, "demo")).unwrap_err();

        assert!(error.contains("Module not found"));
        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn validate_accepts_a_complete_layout() {
        let root = env::temp_dir().join("sct_launcher_test_complete");
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(root.join("apps").join("python_runtime")).unwrap();
        fs::create_dir_all(root.join("apps").join("demo")).unwrap();
        fs::write(root.join("apps").join("python_runtime").join("python.exe"), b"").unwrap();
        fs::write(root.join("apps").join("demo").join("__main__.py"), b"").unwrap();

        assert!(validate(&layout(&root, "demo")).is_ok());
        let _ = fs::remove_dir_all(&root);
    }
}
