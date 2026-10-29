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
 * 面板自身带单实例锁，重复调用无副作用。
 */

'use strict';

const fs = require('fs');
const path = require('path');
const { spawn } = require('child_process');

const EXE = path.join(__dirname, 'WbPet.exe');
const VBS = path.join(__dirname, 'run-panel.vbs');

// 消费掉 stdin，避免上游写管道时 EPIPE
process.stdin.resume();
process.stdin.on('data', () => {});
process.stdin.on('error', () => {});

function launch() {
  try {
    let target = null;
    if (fs.existsSync(EXE)) {
      target = `"${EXE}"`;
    } else if (fs.existsSync(VBS)) {
      target = `wscript.exe "${VBS}"`;
    }
    if (!target) { process.exit(0); }

    // WMI 创建进程，脱离 hook 的 Job Object
    const cmd =
      `$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments ` +
      `@{CommandLine = '${target.replace(/'/g, "''")}'}; ` +
      `exit 0`;
    const wmi = spawn(
      'powershell.exe',
      ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', cmd],
      { stdio: 'ignore', windowsHide: true }
    );
    wmi.on('error', () => {});
    // WMI 调用是异步生效的，给点余量；即使这里退出，WmiPrvSE 也会完成创建
    setTimeout(() => process.exit(0), 3000);
  } catch (_) {
    process.exit(0);
  }
}

launch();
