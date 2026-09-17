# CxShell Portal

这是 CxShell 仓库中的独立静态门户页面，不参与 Avalonia 桌面应用编译，也不需要 Node.js 或构建工具。

## 预览

直接打开 `index.html` 即可预览。页面所需的 Logo、产品截图和捐助二维码都已整理到 `assets/` 下，整个 `website/` 目录可以独立复制和部署。

也可以从仓库根目录启动一个静态文件服务器：

```powershell
python -m http.server 8080 --directory website
```

然后访问 <http://localhost:8080>。

## 内容

- 产品介绍、核心能力与工作区截图
- Avalonia、AtomUI 和 .NET 技术栈说明
- GitHub 仓库、Release 和 Issue 入口
- Ko-fi、微信支付和支付宝捐助入口
