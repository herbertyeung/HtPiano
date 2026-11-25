# 制作 HtPiano 语言包

1. 复制本目录中的 `en.json`，例如命名为 `fr-FR.json`。文件保存为 UTF-8。
2. 把 `id` 改为与文件名一致的语言标记（此例为 `fr-FR`），把 `name` 改为语言菜单里显示的自称（如 `Français`）。
3. 在 `strings` 里只翻译右边的文字，不改左边的键名。保留原文的 `{0}`、`{1}` 等占位符；翻译时可以调换顺序。缺少的条目会用英语。
4. 将文件放进 **HtPiano.exe 同级的 Languages 目录**，重启程序，在左下角的语言菜单选择它。不需要 Visual Studio，也不用编译。

可以先用下面这个最小包试一下。它只翻译「录制」，其余界面仍显示英文：

```json
{
  "id": "fr-FR",
  "name": "Français",
  "strings": {
    "Lang.Record": "Enregistrer"
  }
}
```

开发时将语言文件放在源码的 `HtPiano/Languages/` 目录，构建和发布时会复制到程序旁。钢琴音名、实体按键名和文件扩展名不是界面译文，不必翻译。
