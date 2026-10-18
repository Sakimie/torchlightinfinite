# 0.3.3 已发布验证

版本页：https://github.com/Sakimie/torchlightinfinite/releases/tag/v0.3.3

- 首次升级包 10,398,912 字节，SHA256 `4f85ea98ff28a60b3f30c2e730bdc8e5219b0ad36dbe580c4774a2cb50a9ffbe`。
- 版本包 5,261,277 字节，SHA256 `2c5807aa1c99e4b7353997e53d92f6d11ab7dc5918e5ad54591fe9747284b190`。
- 签名feed 3,363 字节，SHA256 `7e11930bb99b09421f3b9d6e958ab42a9dc35ed97ceaf700a31be96784564406`。

最终ZIP的71项真实Tk/Node集成检查全部通过，含实际旧0.3.2签名安装、新启动器地址迁移、升级与回退。报告 `integration-validation-0.3.3.json` 与此ZIP SHA一致。core/UI/helper、公钥和策略内容均未变；旧0.3.2产物保持不变。

公开发布后，`verify_public_channel.py` 使用生产HTTPS transport，实际获取公开feed、验签、下载并安装0.3.3，再禁止HTTPS验证本地status、回退至0.3.2、返回0.3.3。9项线上与离线恢复检查通过，见 `public-channel-validation.json`。公开三个下载文件均与本地审核字节匹配。

GitHub Release为非draft、非prerelease、当前latest。当前连接的uploads.github.com返回认证错误，故通过Git Data API独立分支提供公开文件，Release正文包含下载链接。版本包固定到完整提交；channel提交保留该提交为parent。没有提取令牌，没有公开签名私钥，正常main分支未修改。

没有执行Windows PE。Windows原生启动、锁分支、实际游戏截图和DPI显示仍需实机确认。本次仅迁移更新通道，没有新增训练收益声明。
