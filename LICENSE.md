# License Notes / 许可说明

> 本文件是对仓库根目录 [`LICENSE`](../LICENSE)（标准 MIT，英文版为法定文本）的补充说明，
> 不修改 LICENSE 本身的任何条款。

## 1. Scope Note / 适用范围说明

### 中文

本 MIT 许可适用于解决方案中除以下项目外的全部代码：

- `src/EarCtrl.Agents.Huawei`        → **GPL-3.0-only**（见该目录 LICENSE）
- `src/EarCtrl.Agents.Huawei.Tests`  → **GPL-3.0-only**

EarCtrl.App（含 WPF-UI 依赖，MIT/Apache 双许可）与 EarCtrl.Core、EarCtrl.Ipc 均不受
GPL 约束。参考项目 OpenFreebuds 与 GalaxyBudsClient 的 GPL-3.0 许可仅影响直接转写其
代码的部分（即上述 Agent 项目）。GPL 部分与 MIT 主程序通过 NamedPipe IPC 进程隔离
（mere aggregation，单纯聚合），互不传染。

### English

This MIT license applies to all code in the solution **EXCEPT**:

- `src/EarCtrl.Agents.Huawei`        → **GPL-3.0-only** (see LICENSE in that folder)
- `src/EarCtrl.Agents.Huawei.Tests`  → **GPL-3.0-only**

EarCtrl.App (including the WPF-UI dependency, dual-licensed MIT/Apache),
EarCtrl.Core and EarCtrl.Ipc are NOT covered by the GPL. The GPL-3.0 licenses of
the reference projects OpenFreebuds and GalaxyBudsClient only affect the parts
directly transcribed from their code (i.e. the Agent projects above). The GPL
parts communicate with the MIT main app over a NamedPipe IPC process boundary
(mere aggregation) and do not copyleft-contaminate each other.

## 2. Chinese Translation of the MIT License / MIT 许可证中文参考译文

> 仅为英文版的参考译文，解释以根目录英文版 LICENSE 为准。
> This is a courtesy translation; the English LICENSE at the repository root
> prevails in case of any discrepancy.

版权所有 (c) 2026 EarCtrl Studio 贡献者 / CelestialDomeStarPole

特此免费授予任何获得本软件及相关文档文件（"本软件"）副本者，无限制地处理本软件的
权利，包括但不限于使用、复制、修改、合并、发布、分发、再许可和/或销售本软件的副本，
并允许获得本软件的人这样做，但须满足以下条件：

上述版权声明与本许可声明应包含在本软件的所有副本或实质部分中。

本软件按"现状"提供，不附带任何明示或默示的担保，包括但不限于适销性与特定用途适用性
的担保。无论是合同、侵权还是其他情形，作者或版权持有人均不对由本软件或本软件的使用
或其他交易引起的任何索赔、损害或其他责任负责。
