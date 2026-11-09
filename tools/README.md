# UmaKeyExport

把游戏的 `meta` 解密后导出 `keys.txt`（`hash<TAB>key`），供重建库同步资源密钥使用。

## 用法

1. 双击运行 `UmaKeyExport.exe`
2. 把 `meta` 拖进窗口

输出写在 meta 同目录的 `keys.txt`。也可以直接把 meta 拖到 exe 上，或点「选择 meta…」。

日志区会打印 cipher/key 设置结果和最终行数，例如：

```
cipher=3 设置结果 rc=3
key 设置结果 rc=0（长度 32）
已写出 339256 行 → C:\...\Persistent\keys.txt
```

## 原理

游戏的 `meta` 是 SQLCipher 加密的（文件头不是 `SQLite format 3`）。解密方式与 UmaViewerBV 的
`UmaDatabaseController.GenFinalKey` 一致：

```csharp
for (int i = 0; i < dbKey.Length; i++)
    finalKey[i] = (byte)(dbKey[i] ^ baseKey[i % 13]);

sqlite3mc_config(db, "cipher", 3);
sqlite3_key(db, finalKey, finalKey.Length);
```

随后执行：

```sql
SELECT h, e FROM a WHERE e IS NOT NULL AND e <> 0;
```

`h` 是资源的内容哈希，`e` 是解密该资源用的 key。JP 服的 `DBKey` / `DBBaseKey` 已内置，无需填写。

## 注意

- 只能处理**带 `e` 列**的原生 meta。Viewer 重建出来的 `meta_umaviewer` 没有这一列，会提示导不出来。
- 单文件自包含，不需要安装 .NET 运行时。未签名，首次运行会有 SmartScreen 提示（更多信息 → 仍要运行）。
