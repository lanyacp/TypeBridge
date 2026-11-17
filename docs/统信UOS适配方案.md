# TypeBridge 统信 UOS 版 · 适配方案

> **实施状态（2026-11）**：阶段 1（安装器发行版识别与文案、selfcheck 发行版感知与
> 统信切 X11 指引）与阶段 3（`build/pack_deb.sh` deb 打包 + README 指引）已完成；
> 引擎版本升到 v0.7。**阶段 0 预研采集与阶段 2 实测仍需 UOS 实机/虚拟机**，
> 按附录 A 清单执行即可。
>
> 结论先行：**高度可行**。麒麟版的 Python 引擎（`kylin/typebridge.py`）本身就是按
> "运行时检测、不写死环境"设计的，UOS 与麒麟同为 Debian 系（apt/dpkg、XDG 标准），
> 引擎层预计 **零改动或极少改动**。工作量集中在三处：安装器适配、deb 打包
> （信创客户普遍要求）、以及 UOS 各版本×各架构的实测验证。
> 总体量估计 **4~6 个工作日**（不含等待测试机的时间），瓶颈是拿到 UOS 机器。

---

## 1. 背景与目标

- 信创市场里麒麟、统信是双雄。已有麒麟版的情况下，"只支持麒麟"在招投标和
  客户现场经常直接出局；补上 UOS 支持是纯增量市场。
- 目标：UOS 桌面专业版（V20 1050/1060/1070，X11 默认）开箱即用；
  UOS V20 Next / V25（Wayland，Treeland 合成器）尽力支持、兜底可切 X11 会话；
  AMD64 / ARM64（飞腾、鲲鹏）实测通过，LoongArch64（龙芯 3A5000+）尽力支持。
- 非目标（本期不做）：UOS 服务器版（无图形桌面，产品用不上）；上架统信应用商店
  （需要开发者认证和审核流程，先做企业内部分发的 deb，商店上架留作后续）。

## 2. 统信 UOS 现状盘点（方案的事实依据）

| 维度 | 现状 | 对 TypeBridge 的影响 |
|---|---|---|
| 版本线 | 主流装机是 V20 专业版（1050/1060/1070）；新装机逐步转 V20 Next / V25 | 两条线都要覆盖 |
| 显示协议 | V20 专业版默认 **X11**，Wayland 为可选会话（1070 起加强）；V20 Next / V25 以 **Wayland** 为主（Treeland 合成器） | X11 路线与麒麟完全一致；Wayland 路线需实测 |
| CPU 架构 | AMD64（Intel/AMD/海光/兆芯）、ARM64（飞腾/鲲鹏）、LoongArch64（龙芯 3A5000+）、早期 MIPS64el、服务器另有 SW64 | Python 天然跨架构；只依赖发行版软件源里有没有 xdotool 等工具包 |
| 包管理 | apt + dpkg，软件源分"主流仓库/渣库"，企业环境常有**内网源** | 安装脚本可直接复用；离线环境需提供依赖打包方案 |
| 桌面环境 | DDE；XDG 标准（autostart/desktop 文件/xdg-user-dir 均支持） | 桌面集成零改造 |
| Python | 系统自带 python3（V20 为 3.7，V25 更新） | 引擎只用标准库，3.6+ 即可，兼容 |

## 3. 麒麟版 vs 统信版：逐项对比

| 能力 | 麒麟版现状 | UOS 上预期 | 改动量 |
|---|---|---|---|
| 文字注入（X11 会话） | xdotool | 同（UOS 源内有 xdotool） | 无 |
| 文字注入（Wayland 会话） | dotool(uinput) > 剪贴板 | dotool / wl-clipboard，**Treeland 兼容性需实测** | 实测+提示语 |
| 剪贴板通道 | xclip / wl-clipboard | 同 | 无 |
| 图片粘贴到光标 | xclip `-t image/*` / wl-copy `--type` | 同，DDE 剪贴板管理器是否干扰需实测 | 实测 |
| 文件接收 | xdg-user-dir DOWNLOAD | 同（DDE 遵循 XDG） | 无 |
| 开机自启 | ~/.config/autostart | 同（DDE 支持） | 无 |
| 桌面图标 | .desktop + gio trusted | 同 | 无 |
| 扫码页/手机端 Web | 纯 HTTP，与发行版无关 | 同 | 无 |
| 多网络选择 / --ip / 网络守望 | 通用 Linux 逻辑 | 同 | 无 |
| 一键安装脚本 | install.sh（apt + zenity） | 复用，增加 UOS 识别与文案 | 小 |
| 环境自检 | --selfcheck | 复用，增加 UOS 提示（含"切回 X11 会话"指引） | 小 |
| 分发形态 | 脚本包（用户目录，免管理员） | 增加 **deb 包**（系统级，IT 统一推送） | 新增 |

一句话：**产品能力全部平移，需要新写的只有"统信安装器 + deb 打包 + 实测记录"**。

## 4. 技术路线：单引擎、多发行版（明确不分叉）

AirType 的教训：分叉容易，合回来疼。统信版**不复制一份代码**，而是：

```
kylin/                        # 现目录（名字保留，内容升级为通用 Linux 引擎）
├── typebridge.py             # 引擎：零改动或仅 selfcheck 提示语微调
├── web/  assets/  run.sh     # 零改动
└── install.sh                # 升级：自动识别发行版（麒麟/统信/其他），分支只有文案与依赖差异
build/
└── pack_deb.sh               # 新增：把引擎打成 deb（见第 6 节）
```

- 发行版识别：读 `/etc/os-release`。麒麟是 `ID=kylin`（或 `ukylin`），统信是
  `ID=uos` / `ID_LIKE` 含 `deepin`，都不认识时按"通用 Linux"走（其实也能跑）。
- 安装器只在三处分支：欢迎页文案、依赖缺失提示（提示管理员装哪个包）、
  自检失败时的指引（统信 Wayland 会话多一条"可在 控制中心→显示 切回 X11"）。
- `install.sh` 里已有的逻辑（pkexec 补依赖、zenity 降级、桌面图标、autostart、
  修复入口）全部通用，不动。

## 5. 注入通道矩阵与实测计划

| 会话/版本 | 首选通道 | 兜底 | 风险 |
|---|---|---|---|
| UOS V20 专业版（X11，默认） | xdotool | 剪贴板(xclip) | 低：与麒麟同路径 |
| UOS V20 1070+ Wayland 可选会话（KWin） | dotool(uinput) | wl-clipboard+按键 | 中：KWin 支持 wlr 协议，wl-copy 理论可用 |
| UOS V20 Next / V25（Wayland，Treeland） | dotool(uinput) | wl-clipboard | **高**：Treeland 对 wlr-data-control 的支持需实测；不行则文档引导切 X11 会话 |
| 任意会话 × ARM64/LoongArch64 | 同上 | 同上 | 中：工具包是否在对应架构源里需采集 |

- `dotool` 不在 UOS 官方源里：安装器检测到 gcc 时提供"源码现场编译"选项
  （dotool 是单文件 C 程序，`make` 十秒）；无编译器的企业环境用剪贴板通道兜底。
- 企业安全管控风险：部分信创单位开内核 lockdown / uinput 白名单，dotool 会被
  卡住。对策：`--selfcheck` 明确报"uinput 无权限，请找管理员加 input 组或放行"，
  剪贴板通道照常可用（只影响"免剪贴板"体验，不影响能用）。

## 6. deb 打包方案（信创客户的硬需求）

很多信创客户不允许用户跑安装脚本，只接受 IT 从内部源统一推送 deb。

```
typebridge_vX.Y_all/
├── DEBIAN/
│   ├── control          # Package: typebridge / Version / Architecture: all
│   │                    # Depends: python3, xdotool | wl-clipboard, xclip
│   └── postinst         # 装完提示；不动用户目录，首次运行时自建 ~/.config
├── usr/bin/typebridge              # 一行 wrapper: exec python3 /usr/share/typebridge/typebridge.py "$@"
├── usr/share/typebridge/           # typebridge.py + web/
├── usr/share/applications/typebridge.desktop
└── usr/share/icons/hicolor/scalable/apps/typebridge.svg
```

- 用 `dpkg-deb --build` 直接打（纯文件包，不需要 debhelper 全家桶），一个
  `build/pack_deb.sh` 搞定，`Architecture: all` 一包通吃所有 CPU 架构。
- 与脚本安装版**并存不冲突**：deb 装到 `/usr/share/typebridge`，脚本装到
  `~/.local/share/typebridge`；引擎启动时无感，配置都在 `~/.config/typebridge`。
- `install.sh --fix-deps` 修复入口同步认得 deb 安装路径（优先系统目录）。
- 内网源：文档给出 `reprepro` 建源三行命令；要不要 GPG 签名由客户 IT 定。

## 7. 改造清单（按阶段）

**阶段 0 · 预研（0.5~1 天，需 UOS 机器/虚拟机）**
拿到机器先跑采集清单（见附录 A），把 `--selfcheck` 输出、软件源里
xdotool/wl-clipboard/xclip 的 `apt-cache policy` 结果带回来，据此冻结通道矩阵。

**阶段 1 · 安装器与引擎适配（约 1 天）**
- `install.sh`：发行版识别 + 统信文案 + 依赖提示按 UOS 包名校对；
- `typebridge.py --selfcheck`：环境行加发行版识别；Wayland 失败提示加
  "UOS 可在 控制中心 切回 X11 会话"；
- README 增加"统信 UOS"章节。

**阶段 2 · 实测与修复（1~2 天）**
按第 8 节测试矩阵过一遍；重点盯 Wayland 会话的剪贴板通道、DDE 剪贴板
管理器对"图片上剪贴板"的干扰、开机自启在 DDE 登录管理器下的时序。

**阶段 3 · deb 打包（约 1 天）**
`pack_deb.sh` + 内网源文档 + 在 amd64 实机装 deb 验证升级/卸载。

**阶段 4 · 发布（0.5 天）**
README/发布说明、Gitee 打 tag，产物三件套：脚本包、deb、校验说明。

## 8. 测试矩阵与验收标准

矩阵（按优先级）：
1. UOS V20 专业版 1070，AMD64，X11 —— 主路径，必须全绿
2. 同上 + ARM64（飞腾 D2000 或鲲鹏）—— 信创主力硬件
3. UOS V20 1070 Wayland 可选会话，AMD64 —— 尽力
4. UOS V25 / V20 Next，AMD64 —— 尽力，失败给"切 X11"指引
5. LoongArch64（如有渠道）—— 冒烟即可

每格跑同一套验收（与麒麟版一致）：
- `--selfcheck` 结论通道可用，无红色警示
- 手机扫码 → 打字/实时同步/回车/退格/撤回
- 图片→电脑 自动粘贴；文件传输 200MB 内
- 接收目录自定义（--recv-dir）、开机自启、`^` 退出后重连
- 多网卡机器 `--ip` 切换、网络变化自动跟随

## 9. 风险与对策

| 风险 | 概率 | 对策 |
|---|---|---|
| Treeland(V25) 下 wl-clipboard 不可用 | 中 | 实测确认；不可用则文档引导切 X11 会话（UOS 官方保留该开关）；中期专项适配 |
| 企业安全管控禁 uinput | 中 | 剪贴板通道兜底 + selfcheck 给出找管理员的具体话术 |
| ARM64/LoongArch 源缺工具包 | 低~中 | 预研采集；缺则按架构附带预编译包或离线依赖 bundle |
| 客户内网无外网源 | 高（信创常态） | 提供 `apt download` 离线依赖打包说明；deb 的 Depends 由内源解决 |
| DDE 剪贴板管理器干扰粘贴 | 低 | 实测；必要时引导关闭"剪贴板收集" |
| 无 UOS 测试机 | — | 见第 10 节资源清单，这是唯一硬瓶颈 |

## 10. 需要的资源

- UOS V20 专业版（1070 优先）AMD64 一台：真机或虚拟机均可（虚拟机就能覆盖
  阶段 0/1/3，阶段 2 建议真机）。
- 有条件再借：飞腾/鲲鹏 ARM64 一台、龙芯一台——没有就先标"待验证"，不阻塞发布。
- 一部手机（现有即可）。

## 附录 A · 预研采集清单（拿到机器后逐条执行并保存输出）

```bash
cat /etc/os-release                         # 确认 ID/VERSION，写进发行版识别
echo "$XDG_SESSION_TYPE / $XDG_CURRENT_DESKTOP / $DISPLAY / $WAYLAND_DISPLAY"
loginctl show-session $(loginctl | awk -v u=$(whoami) '$3==u{print $1;exit}') -p Type
python3 --version                           # 引擎兼容性（需 3.6+）
apt-cache policy xdotool xclip wl-clipboard zenity   # 依赖包是否在源里、什么版本
ls -l /dev/uinput; id | grep -o input       # dotool 通道前置条件
dpkg --print-architecture                   # 架构确认
xdg-user-dir DOWNLOAD                       # 接收目录落点
ls ~/桌面 ~/Desktop 2>/dev/null             # 桌面目录名（安装器已做双探测，记录即可）
python3 kylin/typebridge.py --selfcheck     # 直接跑麒麟版引擎的完整自检
```

> 最后一行是整个预研的核心：如果麒麟版引擎在 UOS 上 selfcheck 直接绿，
> 本方案的工作量就只剩安装器、deb 和实测记录了。

## 参考资料

- 统信官方 FAQ：Treeland 合成器与 V20 Next 的 Wayland 方案（faq.uniontech.com）
- 统信官方资源中心：各版本 × 各架构下载矩阵（chinauos.com/resource）
- 维基百科：统信操作系统条目（版本线与 CPU 支持综述）
