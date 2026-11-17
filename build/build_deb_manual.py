#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
deb 手工组装器 —— pack_deb.sh 在没有 dpkg-deb 的环境（如 Windows）下的兜底路径。

按 deb(5) 规范组装：<deb> = ar 归档{ debian-binary, control.tar.gz, data.tar.gz }
与 dpkg-deb 官方输出对齐的细节：
  - ar 成员名不带尾部 "/"，成员按 2 字节对齐（\n 补齐），mtime 固定 0（可复现构建）
  - tar 条目带 "./" 前缀（GNU_FORMAT），文件归属 root:root
  - 可执行位按规则显式设定（Windows 的 stat 不认识无扩展名脚本的可执行位）

注意：本组装未经真实 dpkg 安装验证；分发前建议先在一台 Debian 系机器上
`sudo apt install ./包名.deb` 验装一遍，或用 dpkg-deb 正式构建。

用法：python3 build_deb_manual.py <stage目录> <pkg根目录> <输出.deb>
"""
import os
import sys
import tarfile

# 数据区里必须带可执行位的文件（相对 data.tar 的条目名）
EXEC_ENTRIES = {
    "./usr/bin/typebridge",
    "./usr/share/typebridge/typebridge.py",
}
# 控制区里的维护脚本（dpkg 要求可执行）
EXEC_CONTROL = {"postinst", "postrm", "prerm", "preinst"}


def rootify(ti):
    """统一归属 root:root（Windows/MSYS 的 stat 归属不可信）。"""
    ti.uid, ti.gid = 0, 0
    ti.uname, ti.gname = "root", "root"
    return ti


def control_filter(ti):
    rootify(ti)
    ti.mode = 0o755 if os.path.basename(ti.name) in EXEC_CONTROL else 0o644
    return ti


def data_filter(ti):
    rootify(ti)
    if ti.isdir() or ti.name in EXEC_ENTRIES:
        ti.mode = 0o755
    else:
        ti.mode = 0o644
    return ti


def make_tar(tar_path, src, arcname, filt):
    with tarfile.open(tar_path, "w:gz", format=tarfile.GNU_FORMAT) as tf:
        tf.add(src, arcname=arcname, recursive=True, filter=filt)


def ar_member(name, data):
    """单个 ar 成员 = 60 字节头 + 数据（奇数长度补 \\n 对齐）。"""
    hdr = name.ljust(16).encode("ascii")       # 成员名
    hdr += b"0".ljust(12)                      # mtime = 0
    hdr += b"0".ljust(6)                       # uid
    hdr += b"0".ljust(6)                       # gid
    hdr += b"644".ljust(8)                     # mode
    hdr += str(len(data)).ljust(10).encode("ascii")  # size
    hdr += b"`\n"                              # 结束符
    assert len(hdr) == 60, "ar 头长度错误: %d" % len(hdr)
    if len(data) % 2:
        data += b"\n"
    return hdr + data


def main():
    if len(sys.argv) != 4:
        print("用法: build_deb_manual.py <stage目录> <pkg根目录> <输出.deb>")
        return 2
    stage, pkg, out = sys.argv[1], sys.argv[2], sys.argv[3]
    tmp = os.path.join(stage, "assemble")
    os.makedirs(tmp, exist_ok=True)

    control_tgz = os.path.join(tmp, "control.tar.gz")
    data_tgz = os.path.join(tmp, "data.tar.gz")
    make_tar(control_tgz, os.path.join(pkg, "DEBIAN"), ".", control_filter)
    make_tar(data_tgz, os.path.join(pkg, "usr"), "./usr", data_filter)

    if os.path.exists(out):
        os.remove(out)
    with open(out, "wb") as f:
        f.write(b"!<arch>\n")
        f.write(ar_member("debian-binary", b"2.0\n"))
        with open(control_tgz, "rb") as c:
            f.write(ar_member("control.tar.gz", c.read()))
        with open(data_tgz, "rb") as d:
            f.write(ar_member("data.tar.gz", d.read()))
    print("[OK] 手工组装完成: %s (%d 字节)" % (out, os.path.getsize(out)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
