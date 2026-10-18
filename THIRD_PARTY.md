# 第三方依赖与参考资料

## v0.2 的运行组成

本版本的规则引擎、求解器和离线识图特征算法由本项目独立实现，入口为 `lib/runtime.js`。运行时不下载或加载第三方模拟器检出，也不使用第三方预制识图特征或随附游戏素材；识图输入由用户自行截图或导入。Windows ZIP 不包含以下参考项目的代码、特征数据、美术或攻略原文图片。用户通过本机截图确认后学习的数值特征保存在其本地配置中。

曾用于理解玩法、核对公开机制的资料：

- Tartarus Simulator，CoffeeBns：<https://github.com/blahdot3h/tartarus-simulator>；研究时查看的提交为 `c648a4f286c2b17d709a7201bfc1570c3132531d`。该提交未发现根 LICENSE/COPYING/NOTICE，桌面包标注 UNLICENSED/private，本项目不将公开可读视为再分发授权。
- 玩家攻略：<https://docs.qq.com/doc/DV1VYUE95YWxJekVU?nlc=1>。本项目使用自行编写的机制说明与近似实现，不打包文档正文或内嵌图片。

以上资料仅为参考来源，不是 v0.2 的运行依赖。开门、替换、事件概率及部分结算顺序仍有模型假设，公开资料不能替代实机验证。

## 软件依赖

- **Electron 44.8.0**：桌面运行时，MIT 许可证；Electron 所含 Chromium 等组件还具有各自的第三方许可，分发时应保留运行时随附的完整许可与声明。
- **pngjs 7.0.0**：本机 PNG 解码，MIT 许可证。
- **electron-builder 26.15.3**：开发打包工具，MIT 许可证。其他直接、间接开发依赖按各自随附许可证使用。

Electron 与 pngjs 的许可正文如下；本文件随应用一并分发。运行时和依赖中原有的许可证、版权及第三方声明也应保留。

### Electron

Copyright (c) Electron contributors
Copyright (c) 2013-2020 GitHub Inc.

Permission is hereby granted, free of charge, to any person obtaining
a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

### pngjs

pngjs original work Copyright (c) 2015 Luke Page & Original Contributors
pngjs derived work Copyright (c) 2012 Kuba Niegowski

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
