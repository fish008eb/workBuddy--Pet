#!/usr/bin/env node
/**
 * 由 SessionStart hook 调用，拉起用量悬浮窗。
 *
 * 两个关键点：
 * 1) WorkBuddy 把 hook 进程放进 Windows Job Object，hook 结束后整棵进程树
 *    （含 detached 子进程）会被清理。所以这里通过 WMI Win32_Process.Create
 *    间接启动，新进程父代是 WmiPrvSE.exe，不在 hook 的 Job 里。
 * 2) 面板宿主必须是 GUI 子系统，否则每次启动都会闪一个控制台窗口。
 *    优先用编译好的 WbPet.exe；没有它时退回 wscript + VBS（同样无控制台）。
 *
 * v1.3 修复（面板不显示）：
 *   WorkBuddy 5.9.x 的 hook 进程 PATH 被裁剪，里面没有 System32，
 *   spawn('powershell.exe') 直接 ENOENT（turn-usage.log 里同款报错），
 *   于是这段 WMI 启动从来没真正执行过，面板自然永远不会出现。
 *   现在：powershell 用绝对路径解析；失败回退直接启动；全过程写日志。
 *
 * 面板自身带单实例锁，重复调用无副作用。
 */

'use strict';

const fs = require('fs');
const path = require('path');
const { spawn } = require('child_process');

const DIR = __dirname;
const EXE = path.join(DIR, 'WbPet.exe');
const VBS = path.join(DIR, 'run-panel.vbs');
const LOG = path.join(DIR, 'start-panel.log');
const MAX_LOG = 256 * 1024;

// 消费掉 stdin，避免上游写管道时 EPIPE
process.stdin.resume();
process.stdin.on('data', () => {});
process.stdin.on('error', () => {});

function log(msg) {
  try {
    try {
      if (fs.statSync(LOG).size > MAX_LOG) { fs.writeFileSync(LOG, ''); }
    } catch (_) {}
    fs.appendFileSync(LOG, `[${new Date().toISOString()}] ${msg}\r\n`, 'utf8');
  } catch (_) {}
}

function fileExists(p) {
  try { return fs.existsSync(p); } catch (_) { return false; }
}

function windowsRoot() {
  return process.env.SystemRoot || process.env.WINDIR || 'C:\\Windows';
}

function system32(rel) {
  return path.join(windowsRoot(), 'System32', rel);
}

/** hook 环境 PATH 不含 System32，必须走绝对路径 */
function resolvePowerShell() {
  const cands = [
    system32(path.join('WindowsPowerShell', 'v1.0', 'powershell.exe')),
    path.join(windowsRoot(), 'Sysnative', 'WindowsPowerShell', 'v1.0', 'powershell.exe'),
  ];
  for (const c of cands) {
    if (fileExists(c)) { return c; }
  }
  // 最后才退回裸文件名（PATH 正常时可用）
  return 'powershell.exe';
}

function resolveWscript() {
  const p = system32('wscript.exe');
  return fileExists(p) ? p : 'wscript.exe';
}

/** WMI 创建进程，脱离 hook 的 Job Object；返回是否成功 */
function launchViaWmi(target) {
  return new Promise((resolve) => {
    const ps = resolvePowerShell();
    let done = false;
    let timer = null;

    const finish = (ok, why) => {
      if (done) { return; }
      done = true;
      if (timer) { clearTimeout(timer); }
      log(`WMI 启动${ok ? '成功' : '失败'}：${target}${why ? '（' + why + '）' : ''} | ps=${ps}`);
      resolve(ok);
    };

    // 单引号转义：PowerShell 单引号字符串里 ' 要写成 ''
    const cmd =
      `$ErrorActionPreference='Stop'; ` +
      `$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments ` +
      `@{ CommandLine = '${target.replace(/'/g, "''")}' }; ` +
      `if ($r.ReturnValue -ne 0) { exit $r.ReturnValue }`;

    let child;
    try {
      child = spawn(
        ps,
        ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', cmd],
        { stdio: 'ignore', windowsHide: true }
      );
    } catch (e) {
      log('spawn powershell 异常: ' + e.message);
      resolve(false);
      return;
    }

    child.on('error', (e) => finish(false, 'spawn: ' + e.message));
    child.on('exit', (code) => finish(code === 0, 'exit=' + code));
    timer = setTimeout(() => finish(false, 'timeout'), 8000);
    if (timer.unref) { timer.unref(); }
  });
}

/** 最后的兜底：直接起进程（可能被 hook 的 Job 回收，但总比不试好） */
function launchDirect(file, args) {
  try {
    const child = spawn(file, args, { detached: true, stdio: 'ignore', windowsHide: true });
    child.on('error', (e) => log('直接启动失败: ' + e.message));
    child.unref();
    log('已尝试直接启动: ' + file + (args.length ? ' ' + args.join(' ') : ''));
    return true;
  } catch (e) {
    log('直接启动异常: ' + e.message);
    return false;
  }
}

async function launch() {
  let target = null;
  let directFile = null;
  let directArgs = [];

  if (fileExists(EXE)) {
    target = `"${EXE}"`;
    directFile = EXE;
  } else if (fileExists(VBS)) {
    target = `"${resolveWscript()}" "${VBS}"`;
    directFile = resolveWscript();
    directArgs = [VBS];
  }

  if (!target) {
    log('既没有 WbPet.exe 也没有 run-panel.vbs，放弃启动');
    process.exit(0);
  }

  log('开始启动面板: ' + target);
  const ok = await launchViaWmi(target);
  if (!ok) {
    log('WMI 路径不可用，回退直接启动');
    launchDirect(directFile, directArgs);
  }

  // 给 WMI / 进程创建留一点余量再退出，避免 hook 结束得太快
  setTimeout(() => process.exit(0), ok ? 600 : 1200);
}

launch();
