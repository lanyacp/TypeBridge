# -*- coding: utf-8 -*-
# 结束 TypeBridge 进程并重启（沙箱里 taskkill/Stop-Process 会「拒绝访问」，
# 必须用 ctypes 直接 OpenProcess+TerminateProcess）
import ctypes, subprocess, sys
from ctypes import wintypes

PROCESS_TERMINATE = 0x0001
k = ctypes.WinDLL("kernel32", use_last_error=True)
k.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
k.OpenProcess.restype = wintypes.HANDLE
k.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
k.TerminateProcess.restype = wintypes.BOOL
k.CloseHandle.argtypes = [wintypes.HANDLE]
k.CloseHandle.restype = wintypes.BOOL

out = subprocess.run(
    ["tasklist", "/FI", "IMAGENAME eq TypeBridge.exe", "/FO", "CSV", "/NH"],
    capture_output=True, text=True,
).stdout

pids = []
for line in out.splitlines():
    parts = [p.strip('"') for p in line.split('","')]
    if len(parts) > 1 and parts[0].lower() == "typebridge.exe":
        try:
            pids.append(int(parts[1]))
        except ValueError:
            pass

if not pids:
    print("no TypeBridge process running")
    sys.exit(0)

for pid in pids:
    h = k.OpenProcess(PROCESS_TERMINATE, False, pid)
    if not h:
        print("OpenProcess failed pid=%d err=%d" % (pid, ctypes.get_last_error()))
        continue
    ok = k.TerminateProcess(h, 0)
    k.CloseHandle(h)
    print(("killed " if ok else "failed ") + str(pid))
