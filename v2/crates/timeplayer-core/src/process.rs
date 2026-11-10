//! Runs yt-dlp, ffmpeg and other programs (1.x's `DefaultProcessRunner`): arguments passed as a list
//! (never through a shell), no console window, output read line by line, and the program stopped on
//! timeout or cancel.

use std::io::{BufRead, BufReader, Read};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use crate::error::{AppError, Result};
use crate::http::Cancel;

#[derive(Debug, Clone, Default)]
pub struct RunOptions {
    pub timeout: Option<Duration>,
    pub working_directory: Option<PathBuf>,
    pub env: Vec<(String, String)>,
}

#[derive(Debug, Clone, Default)]
pub struct Output {
    pub exit_code: i32,
    pub stdout: String,
    pub stderr: String,
    pub duration: Duration,
}

impl Output {
    pub fn success(&self) -> bool {
        self.exit_code == 0
    }
}

pub type LineCallback<'a> = &'a (dyn Fn(&str) + Sync);

/// Runs a program. The real one is [`SystemRunner`]; tests use closures.
pub trait Runner: Send + Sync {
    fn run(&self, program: &Path, args: &[String], options: &RunOptions, on_line: Option<LineCallback>, cancel: &Cancel) -> Result<Output>;
}

impl<F> Runner for F
where
    F: Fn(&Path, &[String]) -> Result<Output> + Send + Sync,
{
    fn run(&self, program: &Path, args: &[String], _: &RunOptions, on_line: Option<LineCallback>, _: &Cancel) -> Result<Output> {
        let output = self(program, args)?;
        if let Some(cb) = on_line {
            output.stdout.lines().for_each(cb);
        }
        Ok(output)
    }
}

pub struct SystemRunner;

/// No console window for programs started from a window app.
pub fn hide_window(command: &mut Command) {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        command.creation_flags(CREATE_NO_WINDOW);
    }
    #[cfg(not(windows))]
    let _ = command;
}

impl Runner for SystemRunner {
    fn run(&self, program: &Path, args: &[String], options: &RunOptions, on_line: Option<LineCallback>, cancel: &Cancel) -> Result<Output> {
        let started = Instant::now();
        let mut command = Command::new(program);
        command.args(args).stdin(Stdio::null()).stdout(Stdio::piped()).stderr(Stdio::piped());
        if let Some(dir) = &options.working_directory {
            command.current_dir(dir);
        }
        for (k, v) in &options.env {
            command.env(k, v);
        }
        hide_window(&mut command);
        let mut child = command.spawn().map_err(|e| AppError::new("PROCESS_START", format!("{} could not start: {e}", program.display())))?;

        let stdout = child.stdout.take();
        let stderr = child.stderr.take();
        let out_text = Arc::new(Mutex::new(String::new()));
        let err_text = Arc::new(Mutex::new(String::new()));
        let (tx, rx) = std::sync::mpsc::channel::<String>();
        let out_reader = stdout.map(|s| {
            let collected = out_text.clone();
            std::thread::spawn(move || {
                for line in BufReader::new(s).lines().map_while(|l| l.ok()) {
                    let mut text = collected.lock().unwrap();
                    text.push_str(&line);
                    text.push('\n');
                    let _ = tx.send(line);
                }
            })
        });
        let err_reader = stderr.map(|mut s| {
            let collected = err_text.clone();
            std::thread::spawn(move || {
                let mut buf = String::new();
                let _ = s.read_to_string(&mut buf);
                collected.lock().unwrap().push_str(&buf);
            })
        });

        let exit_code = loop {
            while let Ok(line) = rx.try_recv() {
                if let Some(cb) = on_line {
                    cb(&line);
                }
            }
            if let Some(status) = child.try_wait()? {
                break status.code().unwrap_or(-1);
            }
            let timed_out = options.timeout.is_some_and(|t| started.elapsed() > t);
            if timed_out || cancel.is_canceled() {
                kill_tree(&mut child);
                let _ = child.wait();
                if let Some(r) = out_reader {
                    let _ = r.join();
                }
                return Err(if timed_out {
                    AppError::new("TIMEOUT", format!("{} took too long and was stopped.", program.file_name().unwrap_or_default().to_string_lossy()))
                } else {
                    AppError::canceled()
                });
            }
            std::thread::sleep(Duration::from_millis(40));
        };
        if let Some(r) = out_reader {
            let _ = r.join();
        }
        if let Some(r) = err_reader {
            let _ = r.join();
        }
        while let Ok(line) = rx.try_recv() {
            if let Some(cb) = on_line {
                cb(&line);
            }
        }
        let stdout = out_text.lock().unwrap().clone();
        let stderr = err_text.lock().unwrap().clone();
        Ok(Output { exit_code, stdout, stderr, duration: started.elapsed() })
    }
}

/// Stops a program and anything it started (yt-dlp starts ffmpeg).
pub fn kill_tree(child: &mut std::process::Child) {
    #[cfg(windows)]
    {
        let mut command = Command::new("taskkill");
        command.args(["/PID", &child.id().to_string(), "/T", "/F"]).stdout(Stdio::null()).stderr(Stdio::null());
        hide_window(&mut command);
        if command.status().is_ok_and(|s| s.success()) {
            return;
        }
    }
    let _ = child.kill();
}

/// The first copy of `name` on the PATH ("node.exe", "ffmpeg").
pub fn find_on_path(name: &str) -> Option<PathBuf> {
    let path = std::env::var_os("PATH")?;
    std::env::split_paths(&path).map(|dir| dir.join(name)).find(|p| p.is_file())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn runs_a_program_and_reads_its_lines() {
        #[cfg(windows)]
        let (program, args) = (PathBuf::from("cmd"), vec!["/C".to_string(), "echo one& echo two".to_string()]);
        #[cfg(not(windows))]
        let (program, args) = (PathBuf::from("sh"), vec!["-c".to_string(), "echo one; echo two".to_string()]);
        let lines = Mutex::new(Vec::new());
        let out = SystemRunner.run(&program, &args, &RunOptions::default(), Some(&|l: &str| lines.lock().unwrap().push(l.trim().to_owned())), &Cancel::new()).unwrap();
        assert!(out.success());
        assert_eq!(lines.into_inner().unwrap(), ["one", "two"]);
    }
}
