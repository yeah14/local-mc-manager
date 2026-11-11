# local-mc-manager

Windows 上的本机 Minecraft 服务端管理器。它扫描一个目录里的各个服务端文件夹，用来启动、查看日志、备份世界和修改配置。朋友可以用浏览器远程启动；主机设置口令后，网页上也能做这些管理。

## 直接运行

发布包不需要另外安装 .NET。

1. 下载 [local-mc-manager-1.0.0-win-x64.zip](https://github.com/yeah14/local-mc-manager/raw/v1.0.0/local-mc-manager-1.0.0-win-x64.zip)。
2. 解压。
3. 运行 `McPanel.exe`。

把程序放在所有服务端文件夹的上一层时，它会自己找到这些服务端。找不到时，在窗口里选择那个总目录。每个服务端文件夹里需要有 `server.properties`，或者 `run.bat`、`start.bat`。

关闭管理器不会停止已经启动的 Minecraft 服务器，但网页入口会一起关掉。

## 管理器能做的事

- 分别启动各个服务端，并显示地址、端口和运行状态。
- 在窗口里查看日志。崩溃时保存崩溃记录。
- 导入、替换和备份世界。
- 用中文名称查看和修改 `server.properties`。服务器正在运行时只能查看。

`远程启动.bat` 打开同一台电脑上的远程启动窗口，用来连接另一台主机上的管理器。

## 网页远程

在管理器里打开远程启动并保存后，其他人用浏览器访问 `http://主机IP:28765`。默认端口是 28765，防火墙需要放行这个端口。

- CPU 和内存都低于主机设置的阈值时，网页可以直接启动。任一占用达到阈值时，网页会提示这台电脑正在使用，稍后再试。
- 不输入口令时，网页只能查看地址、端口并启动。
- 主机设置了口令后，输入正确口令可以停止、强制结束、查看日志、发送指令、备份、新建世界、修改配置、从备份恢复，以及查看崩溃记录。
- 主机没有设置口令时，网页上的这些管理请求会被拒绝。启动功能仍然可用。

## 从源码编译

需要 Windows 和 [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)。

在本目录执行：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o publish
```

生成的程序在 `publish\McPanel.exe`。这样打出来的程序同样不需要目标电脑另外安装 .NET。
