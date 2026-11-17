using Character.Cli;

// D1b：核对入口从角色服进程搬到这个 probe。
// 133 个 flag 的解析、只读核对与连库核对都在同目录的 Cli* 类里；
// 不带任何核对 flag 时仍按原样起 CharacterProcess（脚本里的 `--config` 启动段不用改）。
return await CliEntry.RunAsync(args);
